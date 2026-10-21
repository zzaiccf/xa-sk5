#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shellapi.h>

// An intentionally slow, isolated host. It never installs anything or opens the proxy service.
LRESULT CALLBACK TestWindow(HWND handle, UINT message, WPARAM first, LPARAM second) {
    if (message == WM_TIMER) { DestroyWindow(handle); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(handle, message, first, second);
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    int count{}; auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    bool verify = count > 1 && wcscmp(args[1], L"--verify-payload") == 0; LocalFree(args);
    if (verify) return 0;
    wchar_t mode[32]{}; GetEnvironmentVariableW(L"XA_SK5_BOOTSTRAP_FIXTURE", mode, 32);
    if (wcscmp(mode, L"fail") == 0) { Sleep(800); return 42; }
    Sleep(wcscmp(mode, L"slow") == 0 ? 20000 : 1500);
    WNDCLASSW type{}; type.hInstance = instance; type.lpfnWndProc = TestWindow;
    type.lpszClassName = L"XaSk5BootstrapFixture"; type.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    RegisterClassW(&type);
    HWND handle = CreateWindowW(type.lpszClassName, L"xa sk5 模擬安裝精靈（流程驗證）", WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 560, 240, nullptr, nullptr, instance, nullptr);
    ShowWindow(handle, SW_SHOW); UpdateWindow(handle);
    wchar_t name[128]{}; GetEnvironmentVariableW(L"XA_SK5_SETUP_READY_EVENT", name, 128);
    HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
    if (ready) { SetEvent(ready); CloseHandle(ready); }
    SetTimer(handle, 1, 1200, nullptr);
    MSG message{}; while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return 0;
}
