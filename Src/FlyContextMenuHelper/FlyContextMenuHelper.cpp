// FlyContextMenuHelper.cpp
// compile with: /D_UNICODE /DUNICODE /DWIN32 /D_WINDOWS /c

/**
 * @file FlyContextMenuHelper.cpp
 * @brief Implements a small hidden helper process that displays the native Shell context menu
 *        for a given file path on behalf of a host application.
 *
 * @details
 * This executable creates a hidden window that listens for IPC (WM_COPYDATA) containing
 * coordinates and a file path, then uses the ShellContextMenu helper to show the
 * native Windows context menu at the requested position. A monitor thread watches
 * for the host application process and terminates the helper if the host exits.
 */

#include <windows.h>
#include <stdlib.h>
#include <string.h>
#include <tchar.h>
#include <string>
#include <sstream>
#include <vector>
#include "ShellContextMenu.h"

#define WM_SHOW_CTX_MENU (WM_USER + 1)

// Global variables

// The main window class name.
static TCHAR szWindowClass[] = _T("context_menu_helper_fly");

// The string that appears in the application's title bar.
static TCHAR szTitle[] = _T("Context Menu Helper for FlyPhotos");

// Stored instance handle for use in Win32 API calls such as FindResource
HINSTANCE hInst;

// Stop event for monitor thread
static HANDLE g_hStopEvent = NULL;
static HANDLE g_hMonitorThread = NULL;

// Handle to the FlyPhotos.exe process that launched us. The monitor thread blocks on this
// instead of scanning the process table, so the helper is tied to the specific instance that
// started it rather than to any process that happens to share the name.
static HANDLE g_hHostProcess = NULL;

// Forward declarations
LRESULT CALLBACK WndProc(HWND, UINT, WPARAM, LPARAM);
DWORD WINAPI MonitorThreadProc(LPVOID lpParam);

static ShellContextMenu g_shellContextMenu;

/**
 * @brief Parameters used when posting a message to show the context menu.
 *
 * Stored on the heap and passed via WM_SHOW_CTX_MENU to the hidden window so the
 * IPC thread is not blocked while the (blocking) context menu is displayed.
 */
struct ContextMenuParams {
    int x = 0;                   ///< X coordinate in screen coordinates
    int y = 0;                   ///< Y coordinate in screen coordinates
    std::wstring filePath;       ///< Full path of the file to show the context menu for
    HWND hRequesterWnd = 0;      ///< HWND of the requesting window (used to restore focus)
};

/**
 * @brief Opts this process into dark menus so TrackPopupMenuEx follows the Windows theme.
 *
 * @details
 * uxtheme.dll exports these two functions by ordinal only - undocumented, but stable since
 * Win10 1903. PreferredAppMode::AllowDark (1) means "this app supports dark mode, follow the
 * system setting"; ForceDark (2) would ignore the Windows theme, which is not what we want.
 *
 * Called before every menu display rather than once at startup: this helper outlives many
 * right-clicks, and FlushMenuThemes drops uxtheme's cached menu theme so a Windows light/dark
 * switch made in the meantime is picked up on the next menu.
 *
 * @note On Win10 1809 ordinal 135 is the older AllowDarkModeForApp(BOOL); passing 1 means TRUE
 *       there too, so no OS version guard is needed.
 * @note The C# side does the same thing for the in-process menu (ContextMenuHelper.cs) - the
 *       preferred app mode is per-process, so each process must opt in for itself.
 */
static void SyncMenuThemeToWindows()
{
    using SetPreferredAppModeFn = int (WINAPI*)(int);
    using FlushMenuThemesFn = void (WINAPI*)();

    constexpr int ORD_SET_PREFERRED_APP_MODE = 135;
    constexpr int ORD_FLUSH_MENU_THEMES = 136;
    constexpr int ALLOW_DARK = 1;

    // Resolved once; uxtheme stays loaded for the lifetime of the process anyway.
    static HMODULE hUxTheme = LoadLibraryExW(L"uxtheme.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!hUxTheme) return;

    static auto pSetPreferredAppMode = reinterpret_cast<SetPreferredAppModeFn>(
        GetProcAddress(hUxTheme, MAKEINTRESOURCEA(ORD_SET_PREFERRED_APP_MODE)));
    static auto pFlushMenuThemes = reinterpret_cast<FlushMenuThemesFn>(
        GetProcAddress(hUxTheme, MAKEINTRESOURCEA(ORD_FLUSH_MENU_THEMES)));
    if (!pSetPreferredAppMode || !pFlushMenuThemes) return;

    pSetPreferredAppMode(ALLOW_DARK);
    pFlushMenuThemes();
}

/**
 * @brief Application entry point.
 *
 * Creates a hidden helper window and a monitor thread, then enters the message loop.
 * The helper expects WM_COPYDATA messages containing the coordinates and file path
 * (formatted as "x|y|<FilePath>") from the host application.
 *
 * @param hInstance Handle to the current instance.
 * @param hPrevInstance Unused; provided for compatibility (always NULL in Win32).
 * @param lpCmdLine Command line string (ANSI); carries the host process ID in decimal.
 * @param nCmdShow Show flag for the window; window is hidden so typically ignored.
 * @return int Exit code from the message loop (from WM_QUIT wParam).
 */
int WINAPI WinMain(
    _In_ HINSTANCE hInstance,
    _In_opt_ HINSTANCE hPrevInstance,
    _In_ LPSTR     lpCmdLine,
    _In_ int       nCmdShow
)
{
    // ENABLE HIGH DPI AWARENESS (Per Monitor V2)
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    WNDCLASSEX wcex = { 0 };
    wcex.cbSize = sizeof(WNDCLASSEX);
    wcex.style = CS_HREDRAW | CS_VREDRAW;
    wcex.lpfnWndProc = WndProc;
    wcex.cbClsExtra = 0;
    wcex.cbWndExtra = 0;
    wcex.hInstance = hInstance;
    wcex.hIcon = LoadIcon(wcex.hInstance, IDI_APPLICATION);
    wcex.hCursor = LoadCursor(NULL, IDC_ARROW);
    wcex.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    wcex.lpszClassName = szWindowClass;
    wcex.hIconSm = LoadIcon(wcex.hInstance, IDI_APPLICATION);

    if (!RegisterClassEx(&wcex)) return 1;

    hInst = hInstance;

    // Init OLE/Shell logic
    g_shellContextMenu.Init();

    // Create hidden window
    HWND hWnd = CreateWindowEx(
        WS_EX_TOOLWINDOW,
        szWindowClass,
        szTitle,
        WS_POPUPWINDOW,
        //CW_USEDEFAULT, CW_USEDEFAULT,
        //500, 100,
        0, 0, 0, 0,
        NULL, NULL, hInstance, NULL
    );

    if (!hWnd) return 1;

    // Keep hidden
    ShowWindow(hWnd, SW_HIDE);
    UpdateWindow(hWnd);

    // Monitor thread to auto-close when the host app dies. FlyPhotos passes its PID as the
    // sole command line argument; SYNCHRONIZE is the only access right needed to wait on it.
    // No usable PID means no monitor thread, so the helper lives until it is killed. The C#
    // side always passes one (ContextMenuHelper.ShowContextMenuUsingExternalProcess), so this
    // only applies to a manual launch.
    const DWORD hostPid = lpCmdLine ? strtoul(lpCmdLine, NULL, 10) : 0;
    if (hostPid != 0) {
        g_hHostProcess = OpenProcess(SYNCHRONIZE, FALSE, hostPid);
    }

    g_hStopEvent = CreateEvent(NULL, TRUE, FALSE, NULL);
    if (g_hStopEvent && g_hHostProcess) {
        g_hMonitorThread = CreateThread(NULL, 0, MonitorThreadProc, (LPVOID)hWnd, 0, NULL);
    }

    // Main message loop
    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    // Cleanup
    if (g_hStopEvent) {
        SetEvent(g_hStopEvent);
    }
    if (g_hMonitorThread) {
        WaitForSingleObject(g_hMonitorThread, 2000);
        CloseHandle(g_hMonitorThread);
        g_hMonitorThread = NULL;
    }
    if (g_hStopEvent) {
        CloseHandle(g_hStopEvent);
        g_hStopEvent = NULL;
    }
    if (g_hHostProcess) {
        CloseHandle(g_hHostProcess);
        g_hHostProcess = NULL;
    }

    return (int)msg.wParam;
}

/**
 * @brief Monitor thread: closes the helper window once the host process exits.
 *
 * Blocks on the host process handle and the stop event together. A process handle becomes
 * signalled when the process terminates, so this needs no polling and no enumeration of the
 * process table - the thread simply sleeps until one of the two things happens.
 *
 * Only the host exiting posts WM_CLOSE; the stop event means the helper is already shutting
 * down on its own.
 *
 * @param lpParam HWND of the helper window (passed as LPVOID).
 * @return DWORD Thread exit code (0 on normal exit).
 */
DWORD WINAPI MonitorThreadProc(LPVOID lpParam)
{
    HWND hWnd = (HWND)lpParam;
    const HANDLE waits[] = { g_hStopEvent, g_hHostProcess };

    if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) == WAIT_OBJECT_0 + 1) {
        // Host process terminated - ask the helper window to close.
        PostMessage(hWnd, WM_CLOSE, 0, 0);
    }
    return 0;
}

/**
 * @brief Window procedure for the hidden helper window.
 *
 * Handles the following messages:
 * - WM_COPYDATA: Receives IPC payload "x|y|<FilePath>", parses it and posts a WM_SHOW_CTX_MENU
 *                to itself with a heap-allocated ContextMenuParams so the IPC sender is not blocked.
 * - WM_SHOW_CTX_MENU: Displays the context menu for the provided path and restores focus to the requester.
 * - WM_PAINT: Minimal painting for completeness (window is hidden normally).
 * - WM_DESTROY: Signals the monitor thread to stop and posts WM_QUIT.
 *
 * @param hWnd Window handle receiving the message.
 * @param message The window message ID.
 * @param wParam Message-specific WPARAM.
 * @param lParam Message-specific LPARAM.
 * @return LRESULT The result to return to DefWindowProc or a message-specific result value.
 */
LRESULT CALLBACK WndProc(HWND hWnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    PAINTSTRUCT ps;
    HDC hdc;
    TCHAR greeting[] = _T("Context Menu Helper for FlyPhotos");

    // Try to let the active Shell Context Menu handle the message first.
    // This ensures icons, submenus, and "Open With" work correctly.
    LRESULT lShellResult = 0;
    if (g_shellContextMenu.HandleWindowMessage(message, wParam, lParam, &lShellResult)) {
        return lShellResult;
    }

    switch (message)
    {
    case WM_COPYDATA:
    {
        PCOPYDATASTRUCT pCDS = (PCOPYDATASTRUCT)lParam;
        if (pCDS && pCDS->lpData && pCDS->cbData > 0) {
            // Treat incoming data as ANSI/UTF-8 byte buffer containing "x|y|<FilePath>"
            std::string s((char*)pCDS->lpData, pCDS->cbData);
            // Remove possible trailing nulls
            while (!s.empty() && s.back() == '\0') s.pop_back();

            size_t p1 = s.find('|');
            size_t p2 = (p1 == std::string::npos) ? std::string::npos : s.find('|', p1 + 1);

            if (p1 != std::string::npos && p2 != std::string::npos) {
                std::string sx = s.substr(0, p1);
                std::string sy = s.substr(p1 + 1, p2 - (p1 + 1));
                std::string spath = s.substr(p2 + 1);

                int x = 0, y = 0;
                try { x = std::stoi(sx); y = std::stoi(sy); }
                catch (...) {}

                // Convert path to wide string (assume UTF-8 or ANSI)
                std::wstring wpath;
                if (!spath.empty()) {
                    int req = MultiByteToWideChar(CP_UTF8, 0, spath.c_str(), (int)spath.size(), NULL, 0);
                    if (req > 0) {
                        wpath.resize(req);
                        MultiByteToWideChar(CP_UTF8, 0, spath.c_str(), (int)spath.size(), &wpath[0], req);
                    }
                    else {
                        // Fallback to ANSI
                        req = MultiByteToWideChar(CP_ACP, 0, spath.c_str(), (int)spath.size(), NULL, 0);
                        if (req > 0) {
                            wpath.resize(req);
                            MultiByteToWideChar(CP_ACP, 0, spath.c_str(), (int)spath.size(), &wpath[0], req);
                        }
                    }
                }

                // Create Params on Heap
                auto* params = new ContextMenuParams();
                params->x = x;
                params->y = y;
                params->filePath = wpath;
                params->hRequesterWnd = (HWND)wParam; // Capture Sender HWND

                // Post to self to unblock the IPC thread
                PostMessage(hWnd, WM_SHOW_CTX_MENU, 0, (LPARAM)params);
                return 1;
            }
        }
        return 0;
    }
    case WM_SHOW_CTX_MENU:
    {
        auto* params = reinterpret_cast<ContextMenuParams*>(lParam);
        if (params) {
            std::vector<std::wstring> fileList = { params->filePath };
            POINT pt = { params->x, params->y };
            HWND hRequesterWnd = params->hRequesterWnd;

            // Match the Windows light/dark setting before the menu is created
            SyncMenuThemeToWindows();

            // Show Menu (Blocks here until menu closed)
            g_shellContextMenu.ShowContextMenu(hWnd, fileList, pt);

            // Restore Focus to WinUI App immediately after menu interaction
            if (hRequesterWnd && IsWindow(hRequesterWnd)) {
                SetForegroundWindow(hRequesterWnd);
                SetActiveWindow(hRequesterWnd);
            }

            delete params;
        }
        return 0;
    }
    case WM_PAINT:
        hdc = BeginPaint(hWnd, &ps);
        // (hidden window) no painting required, but keep for completeness
        TextOut(hdc, 5, 5, greeting, _tcslen(greeting));
        EndPaint(hWnd, &ps);
        break;
    case WM_DESTROY:
        // Signal monitor thread to stop
        if (g_hStopEvent) SetEvent(g_hStopEvent);
        PostQuitMessage(0);
        break;
    default:
        return DefWindowProc(hWnd, message, wParam, lParam);
        break;
    }
    return 0;
}