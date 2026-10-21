#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <sddl.h>
#include <string>
#include <vector>
#include <thread>
#include <atomic>
#include <mutex>
#include <stdexcept>
#include <algorithm>
#include <cwchar>

// This entry point is native: paint feedback before extracting or loading any CLR files.
namespace {
constexpr UINT ProgressMessage = WM_APP + 1, LoadingMessage = WM_APP + 2,
    ReadyMessage = WM_APP + 3, DoneMessage = WM_APP + 4, ErrorMessage = WM_APP + 5, LogMessage = WM_APP + 6;
HWND windowHandle{}, statusLabel{}, progressBar{}, cancelButton{}, logButton{};
HFONT font{}, headingFont{};
HBRUSH background{};
HANDLE cancelled{};
std::thread worker;
std::mutex textMutex;
std::wstring logPath, errorText;
std::atomic<bool> loading{false}, finished{false}, handedOff{false};
DWORD loadStarted{}, resultCode{};
ULONGLONG bootStarted{}, visibleAt{};
bool verifyOnly{};

std::wstring Error(DWORD code) {
    wchar_t* message{};
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, code, 0, reinterpret_cast<wchar_t*>(&message), 0, nullptr);
    std::wstring text = message ? message : L"無法完成操作";
    if (message) LocalFree(message);
    return text + L"（代碼 " + std::to_wstring(code) + L"）";
}
void Require(bool okay, const wchar_t* step) {
    if (!okay) throw std::wstring(step) + L"：" + Error(GetLastError());
}
std::wstring Guid() {
    GUID value{}; if (FAILED(CoCreateGuid(&value))) throw std::wstring(L"無法建立安裝工作識別。");
    wchar_t text[40]{}; StringFromGUID2(value, text, 40); return std::wstring(text + 1, 36);
}
std::wstring Quote(const std::wstring& text) {
    std::wstring result = L"\""; size_t slashes = 0;
    for (wchar_t ch : text) {
        if (ch == L'\\') { ++slashes; continue; }
        result.append(slashes * (ch == L'\"' ? 2 : 1), L'\\'); slashes = 0;
        if (ch == L'\"') result += L'\\'; result += ch;
    }
    result.append(slashes * 2, L'\\'); return result + L"\"";
}
void Log(const std::wstring& text) {
    std::wstring path; { std::lock_guard lock(textMutex); path = logPath; }
    if (path.empty()) return;
    HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME now{}; GetSystemTime(&now); wchar_t stamp[64]{};
    swprintf_s(stamp, L"[%04u-%02u-%02u %02u:%02u:%02u UTC] ", now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond);
    std::wstring line = stamp + text + L"\r\n";
    int size = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
    std::string bytes(size, '\0'); WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), bytes.data(), size, nullptr, nullptr);
    DWORD written{}; WriteFile(file, bytes.data(), size, &written, nullptr); CloseHandle(file);
}
std::wstring PrivateFolder() {
    PWSTR local{}; if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) throw std::wstring(L"無法取得使用者資料夾。");
    std::wstring root = std::wstring(local) + L"\\WindowSocks\\Setup"; CoTaskMemFree(local);
    int create = SHCreateDirectoryExW(nullptr, root.c_str(), nullptr);
    if (create != ERROR_SUCCESS && create != ERROR_ALREADY_EXISTS && create != ERROR_FILE_EXISTS)
        throw std::wstring(L"無法建立準備資料夾：") + Error(create);
    DWORD attributes = GetFileAttributesW(root.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) throw std::wstring(L"準備資料夾不可為符號連結。");
    HANDLE token{}; Require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token), L"讀取使用者");
    DWORD bytes{}; GetTokenInformation(token, TokenUser, nullptr, 0, &bytes); std::vector<BYTE> data(bytes);
    bool read = GetTokenInformation(token, TokenUser, data.data(), bytes, &bytes) != FALSE; CloseHandle(token); Require(read, L"讀取使用者身分");
    LPWSTR sid{}; Require(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid, &sid), L"取得使用者身分");
    std::wstring acl = L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;" + std::wstring(sid) + L")"; LocalFree(sid);
    PSECURITY_DESCRIPTOR descriptor{};
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(acl.c_str(), SDDL_REVISION_1, &descriptor, nullptr), L"準備資料夾權限");
    SECURITY_ATTRIBUTES security{sizeof(security), descriptor, FALSE};
    std::wstring folder = root + L"\\bootstrap-" + Guid();
    bool created = CreateDirectoryW(folder.c_str(), &security) != FALSE; LocalFree(descriptor); Require(created, L"建立私人準備資料夾");
    return folder;
}
bool VisibleChild(DWORD pid) {
    struct Search { DWORD pid; bool visible; } search{pid, false};
    EnumWindows([](HWND handle, LPARAM value) -> BOOL {
        auto* match = reinterpret_cast<Search*>(value); DWORD owner{}; GetWindowThreadProcessId(handle, &owner);
        if (owner == match->pid && IsWindowVisible(handle)) { match->visible = true; return FALSE; }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&search));
    return search.visible;
}
void Run(const std::vector<std::wstring>& args) {
    std::wstring folder, host;
    HANDLE child{}, ready{}, lockedFile = INVALID_HANDLE_VALUE;
    bool readyForUser = false;
    try {
        folder = PrivateFolder(); host = folder + L"\\SetupHost.exe";
        { std::lock_guard lock(textMutex); logPath = folder + L"\\bootstrap.log"; }
        if (!verifyOnly) PostMessageW(windowHandle, LogMessage, 0, 0);
        Log(verifyOnly ? L"Embedded payload verification started without a preparation window." : L"Native preparation started; visible feedback precedes CLR startup.");
        Log(L"NativePid=" + std::to_wstring(GetCurrentProcessId()));
        Log(L"PreparationVisibleMs=" + std::to_wstring(visibleAt));
        auto resource = FindResourceW(nullptr, MAKEINTRESOURCEW(201), RT_RCDATA); Require(resource != nullptr, L"讀取內建安裝程式");
        DWORD size = SizeofResource(nullptr, resource); auto loaded = LoadResource(nullptr, resource);
        const auto* bytes = static_cast<const BYTE*>(LockResource(loaded));
        if (!bytes || size == 0) throw std::wstring(L"安裝檔不完整，請重新下載。");
        HANDLE output = CreateFileW(host.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        Require(output != INVALID_HANDLE_VALUE, L"準備安裝檔案");
        DWORD copyError = ERROR_SUCCESS;
        for (DWORD offset = 0; offset < size;) {
            if (WaitForSingleObject(cancelled, 0) == WAIT_OBJECT_0) { copyError = ERROR_CANCELLED; break; }
            DWORD count = (std::min)(static_cast<DWORD>(1024 * 1024), size - offset), written{};
            if (!WriteFile(output, bytes + offset, count, &written, nullptr) || written != count) { copyError = GetLastError(); if (!copyError) copyError = ERROR_WRITE_FAULT; break; }
            offset += written;
            if (!verifyOnly) PostMessageW(windowHandle, ProgressMessage, offset * 100ull / size, 0);
        }
        if (!copyError && !FlushFileBuffers(output)) copyError = GetLastError(); CloseHandle(output);
        if (copyError == ERROR_CANCELLED) { resultCode = 0; Log(L"Preparation cancelled before loading the host."); throw copyError; }
        if (copyError) throw std::wstring(L"解開安裝程式失敗：") + Error(copyError);
        // Hold the staged executable against replacement while creating its process.
        lockedFile = CreateFileW(host.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        Require(lockedFile != INVALID_HANDLE_VALUE, L"檢查安裝檔案");
        std::wstring eventName = L"Local\\WindowSocks.SetupReady." + Guid();
        ready = CreateEventW(nullptr, TRUE, FALSE, eventName.c_str()); Require(ready != nullptr, L"建立啟動通知");
        Require(SetEnvironmentVariableW(L"XA_SK5_SETUP_READY_EVENT", eventName.c_str()), L"準備啟動通知");
        std::wstring command = Quote(host); for (const auto& arg : args) command += L" " + Quote(arg);
        STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION process{};
        bool started = CreateProcessW(host.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, folder.c_str(), &startup, &process) != FALSE;
        DWORD startError = GetLastError(); SetEnvironmentVariableW(L"XA_SK5_SETUP_READY_EVENT", nullptr);
        if (!started) throw std::wstring(L"無法載入安裝精靈：") + Error(startError);
        child = process.hProcess; CloseHandle(process.hThread); CloseHandle(lockedFile); lockedFile = INVALID_HANDLE_VALUE;
        Log(L"Managed host started; waiting for its visible window.");
        Log(L"ManagedHostStartMs=" + std::to_wstring(GetTickCount64() - bootStarted));
        loadStarted = GetTickCount(); loading = true;
        if (!verifyOnly) PostMessageW(windowHandle, LoadingMessage, 0, 0);
        DWORD limit = verifyOnly ? 300000 : 180000;
        while (true) {
            if (!verifyOnly && (WaitForSingleObject(ready, 0) == WAIT_OBJECT_0 || VisibleChild(process.dwProcessId))) {
                readyForUser = true; handedOff = true; Log(L"Installer window is ready; preparation window handed off.");
                Log(L"InstallerReadyMs=" + std::to_wstring(GetTickCount64() - bootStarted));
                PostMessageW(windowHandle, ReadyMessage, 0, 0); break;
            }
            if (WaitForSingleObject(child, 100) == WAIT_OBJECT_0) {
                GetExitCodeProcess(child, &resultCode);
                if (!verifyOnly) throw std::wstring(L"安裝精靈未能開啟（代碼 ") + std::to_wstring(resultCode) + L"），請查看紀錄後重試。";
                break;
            }
            if (WaitForSingleObject(cancelled, 0) == WAIT_OBJECT_0 || GetTickCount() - loadStarted > limit) {
                // The host has no visible window and installation has not begun. Never stop a handed-off wizard.
                if (!verifyOnly && (WaitForSingleObject(ready, 0) == WAIT_OBJECT_0 || VisibleChild(process.dwProcessId))) continue;
                bool userCancelled = WaitForSingleObject(cancelled, 0) == WAIT_OBJECT_0;
                TerminateProcess(child, ERROR_CANCELLED); WaitForSingleObject(child, INFINITE);
                if (userCancelled) { resultCode = 0; Log(L"Loading cancelled before any installer window appeared."); throw DWORD(ERROR_CANCELLED); }
                throw std::wstring(L"載入安裝精靈逾時。請查看紀錄後重試。");
            }
        }
        if (readyForUser) { WaitForSingleObject(child, INFINITE); GetExitCodeProcess(child, &resultCode); }
        Log(L"Installer exited with code " + std::to_wstring(resultCode) + L".");
    }
    catch (DWORD) { }
    catch (const std::wstring& message) {
        resultCode = 1; Log(message); { std::lock_guard lock(textMutex); errorText = message; }
    }
    catch (...) {
        resultCode = 1; Log(L"Unexpected preparation error.");
        std::lock_guard lock(textMutex); errorText = L"準備安裝時發生錯誤，請查看紀錄後重試。";
    }
    if (lockedFile != INVALID_HANDLE_VALUE) CloseHandle(lockedFile);
    if (child) CloseHandle(child); if (ready) CloseHandle(ready);
    // Only remove this run's staged executable. Keep its small diagnostic log.
    if (!host.empty()) for (int attempt = 0; attempt < 5; ++attempt) {
        if (DeleteFileW(host.c_str()) || GetLastError() == ERROR_FILE_NOT_FOUND) break; Sleep(200);
    }
    Log(L"PreparationWorkerFinished");
    finished = true;
    if (!verifyOnly) PostMessageW(windowHandle, resultCode && !readyForUser ? ErrorMessage : DoneMessage, 0, 0);
}
LRESULT CALLBACK WindowProcedure(HWND handle, UINT message, WPARAM value, LPARAM other) {
    switch (message) {
    case WM_CREATE: {
        auto scale = [handle](int size) { return MulDiv(size, GetDpiForWindow(handle), 96); };
        font = CreateFontW(-scale(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Microsoft JhengHei UI");
        headingFont = CreateFontW(-scale(21), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Microsoft JhengHei UI");
        auto label = CreateWindowW(L"STATIC", L"正在準備安裝 xa sk5", WS_CHILD | WS_VISIBLE, scale(28), scale(25), scale(460), scale(35), handle, nullptr, nullptr, nullptr);
        SendMessageW(label, WM_SETFONT, reinterpret_cast<WPARAM>(headingFont), TRUE);
        statusLabel = CreateWindowW(L"STATIC", L"正在解開安裝程式，請稍候…", WS_CHILD | WS_VISIBLE, scale(28), scale(80), scale(460), scale(62), handle, nullptr, nullptr, nullptr);
        progressBar = CreateWindowW(PROGRESS_CLASSW, nullptr, WS_CHILD | WS_VISIBLE | PBS_SMOOTH, scale(28), scale(150), scale(460), scale(20), handle, nullptr, nullptr, nullptr);
        SendMessageW(progressBar, PBM_SETRANGE32, 0, 100);
        logButton = CreateWindowW(L"BUTTON", L"開啟準備紀錄", WS_CHILD | WS_VISIBLE | WS_DISABLED | BS_PUSHBUTTON, scale(28), scale(195), scale(135), scale(36), handle, reinterpret_cast<HMENU>(1002), nullptr, nullptr);
        cancelButton = CreateWindowW(L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, scale(388), scale(195), scale(100), scale(36), handle, reinterpret_cast<HMENU>(1001), nullptr, nullptr);
        for (HWND control : {statusLabel, logButton, cancelButton}) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        SetTimer(handle, 1, 1000, nullptr); return 0;
    }
    case WM_CTLCOLORSTATIC: SetTextColor(reinterpret_cast<HDC>(value), RGB(31,43,66)); SetBkColor(reinterpret_cast<HDC>(value), RGB(246,248,252)); return reinterpret_cast<LRESULT>(background);
    case ProgressMessage:
        SendMessageW(progressBar, PBM_SETPOS, value, 0);
        SetWindowTextW(statusLabel, (L"正在解開安裝程式… " + std::to_wstring(value) + L"%").c_str()); return 0;
    case LoadingMessage:
        SetWindowLongPtrW(progressBar, GWL_STYLE, GetWindowLongPtrW(progressBar, GWL_STYLE) | PBS_MARQUEE);
        SendMessageW(progressBar, PBM_SETMARQUEE, TRUE, 35);
        SetWindowTextW(statusLabel, L"正在載入安裝精靈，請稍候…"); return 0;
    case WM_TIMER:
        if (loading && !finished && !handedOff && WaitForSingleObject(cancelled, 0) != WAIT_OBJECT_0)
            SetWindowTextW(statusLabel, (L"正在載入安裝精靈… 已等待 " + std::to_wstring((GetTickCount() - loadStarted) / 1000) + L" 秒").c_str());
        return 0;
    case LogMessage: EnableWindow(logButton, TRUE); return 0;
    case ReadyMessage: ShowWindow(handle, SW_HIDE); return 0;
    case ErrorMessage: {
        SendMessageW(progressBar, PBM_SETMARQUEE, FALSE, 0); SetWindowTextW(cancelButton, L"關閉"); EnableWindow(cancelButton, TRUE);
        std::lock_guard lock(textMutex); SetWindowTextW(statusLabel, errorText.c_str()); return 0;
    }
    case DoneMessage: DestroyWindow(handle); return 0;
    case WM_COMMAND:
        if (LOWORD(value) == 1002) { std::lock_guard lock(textMutex); if (!logPath.empty()) ShellExecuteW(handle, L"open", logPath.c_str(), nullptr, nullptr, SW_SHOWNORMAL); return 0; }
        if (LOWORD(value) != 1001) break;
        [[fallthrough]];
    case WM_CLOSE:
        if (finished) DestroyWindow(handle);
        else { SetEvent(cancelled); SetWindowTextW(statusLabel, L"正在取消準備…"); EnableWindow(cancelButton, FALSE); }
        return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(handle, message, value, other);
}
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    bootStarted = GetTickCount64();
    int count{}; auto values = CommandLineToArgvW(GetCommandLineW(), &count); std::vector<std::wstring> args;
    for (int i = 1; values && i < count; ++i) { args.emplace_back(values[i]); if (args.back() == L"--verify-payload") verifyOnly = true; }
    if (values) LocalFree(values);
    cancelled = CreateEventW(nullptr, TRUE, FALSE, nullptr); if (!cancelled) return 1;
    if (verifyOnly) { Run(args); CloseHandle(cancelled); return static_cast<int>(resultCode); }
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_PROGRESS_CLASS}; InitCommonControlsEx(&controls);
    background = CreateSolidBrush(RGB(246,248,252));
    WNDCLASSW windowClass{}; windowClass.lpfnWndProc = WindowProcedure; windowClass.hInstance = instance;
    windowClass.lpszClassName = L"XaSk5SetupPreparation"; windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.hbrBackground = background; windowClass.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(101));
    if (!RegisterClassW(&windowClass)) return 1;
    UINT dpi = GetDpiForSystem(); RECT area{0,0,MulDiv(520,dpi,96),MulDiv(260,dpi,96)};
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    AdjustWindowRectExForDpi(&area, style, FALSE, 0, dpi);
    windowHandle = CreateWindowW(windowClass.lpszClassName, L"xa sk5 — 準備安裝", style,
        (GetSystemMetrics(SM_CXSCREEN)-(area.right-area.left))/2, (GetSystemMetrics(SM_CYSCREEN)-(area.bottom-area.top))/2,
        area.right-area.left, area.bottom-area.top, nullptr, nullptr, instance, nullptr);
    if (!windowHandle) return 1;
    ShowWindow(windowHandle, show == SW_HIDE ? SW_SHOWNORMAL : show); UpdateWindow(windowHandle);
    visibleAt = GetTickCount64() - bootStarted;
    worker = std::thread(Run, args);
    MSG message{}; while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    if (worker.joinable()) worker.join();
    if (font) DeleteObject(font); if (headingFont) DeleteObject(headingFont); DeleteObject(background); CloseHandle(cancelled);
    return static_cast<int>(resultCode);
}
