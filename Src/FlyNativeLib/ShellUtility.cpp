/**
 * @file ShellUtility.cpp
 * @brief Implements the utility functions for interacting with the Windows Shell.
 */

#include "pch.h"
#include "ShellUtility.h"
#include "WicUtility.h"
#include <vector>
#include <string>
#include <windowsx.h>
#include <ole2.h>
#include <shlwapi.h>
#include <shlobj.h>
#include <exdisp.h>
#include <time.h>
#include <shobjidl.h>

using namespace std;

/**
 * @brief Default constructor for ShellUtility.
 */
ShellUtility::ShellUtility()
{
}

/**
 * @brief Default destructor for ShellUtility.
 */
ShellUtility::~ShellUtility()
{
}

/**
 * @brief A simple test function to display a message box.
 */
void ShellUtility::SayThis(const wchar_t* phrase)
{
    MessageBox(nullptr, phrase, L"Sample Title", MB_OK);
}

// Every abandoned path below used to return a bare E_FAIL, so a user reporting "wrong photo order"
// gave us no way to tell which of the exit points fired. These codes travel back through the
// P/Invoke and are logged by the C# caller, naming the exact stage that gave up.
#define FLY_E_ENUM_STAGE(n) MAKE_HRESULT(SEVERITY_ERROR, FACILITY_ITF, 0x200 + (n))

/**
 * @brief Retrieves the full paths of all visible items in the currently active File Explorer window.
 * @details This function uses a series of COM interfaces to navigate from the top-level
 *          shell windows down to the specific folder view of the active tab. It utilizes
 *          the modern IShellItem API (Windows Vista+) to robustly enumerate items,
 *          natively supporting long file paths without fixed buffer limitations.
 */
HRESULT ShellUtility::GetFileListFromExplorerWindow(std::vector<std::wstring>& arr, HWND hwndExplorer)
{
    // Prefer the window the caller captured at process start. Resolving the foreground window here
    // instead is in principle a race with the caller's own window activation — the caller runs this
    // on a background thread — though in practice window activation is far slower than reaching
    // this line, so the sample is almost always still Explorer either way.
    HWND hwndFind = hwndExplorer ? hwndExplorer : GetForegroundWindow();

    // Try to find the active tab window within the foreground Explorer window.
    // Modern Explorer (Win 10/11) uses "ShellTabWindowClass". Older versions might use "TabWindowClass".
    HWND hwndActiveTab = FindWindowEx(hwndFind, nullptr, L"ShellTabWindowClass", nullptr);
    if (hwndActiveTab == nullptr)
        hwndActiveTab = FindWindowEx(hwndFind, nullptr, L"TabWindowClass", nullptr);

    // If no active tab window was found, we cannot proceed.
    if (hwndActiveTab == nullptr)
        return FLY_E_ENUM_STAGE(0); // Launch window has no Explorer tab child

    // Create an instance of IShellWindows to enumerate all open shell (Explorer) windows.
    IShellWindows* psw = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_ALL, IID_IShellWindows, (void**)&psw);
    if (FAILED(hr))
        return hr; // Failed to create ShellWindows instance

    long shellWindowCount = -1;
    if (FAILED(psw->get_Count(&shellWindowCount)))
        shellWindowCount = -1;

    // Properly initialize the VARIANT used to index into the shell windows collection.
    VARIANT v;
    VariantInit(&v);
    V_VT(&v) = VT_I4;

    IDispatch* pdisp = nullptr;

    // How far the most promising candidate window got before we gave up on it. Returned to the
    // caller when nothing was collected, so failures are diagnosable from a log alone.
    int stage = 1;

    // IShellWindows is a SPARSE collection, not a packed list: a slot whose window is still
    // registering (RegisterPending) carries no IDispatch yet, so Item() answers it with S_FALSE
    // while get_Count goes on counting it. Treating the first such slot as "end of collection" is
    // what broke issue #228 — the reporter had 14 registered windows, slot 6 was a hole, and
    // enumeration stopped there, never reaching the launch window sitting further along. Walk
    // every slot and skip the holes.
    //
    // ponytail: bound the walk by get_Count. If get_Count itself fails (never seen in practice)
    // fall back to a fixed ceiling rather than risking an unbounded loop.
    const long slotCount = shellWindowCount >= 0 ? shellWindowCount : 256;

    for (V_I4(&v) = 0; V_I4(&v) < slotCount; V_I4(&v)++)
    {
        pdisp = nullptr;
        if (psw->Item(v, &pdisp) != S_OK || pdisp == nullptr)
        {
            if (pdisp) pdisp->Release();
            continue;
        }

        // Each shell window is an IDispatch; query it for the IWebBrowserApp interface.
        IWebBrowserApp* pwba = nullptr;
        hr = pdisp->QueryInterface(IID_IWebBrowserApp, (void**)&pwba);
        pdisp->Release();
        if (FAILED(hr))
            continue;

        // Get the top-level window handle (HWND) of the current IWebBrowserApp.
        HWND hwndWBA = nullptr;
        hr = pwba->get_HWND((LONG_PTR*)&hwndWBA);
        // If this isn't our target window, release and continue the loop.
        if (FAILED(hr) || hwndWBA != hwndFind)
        {
            pwba->Release();
            continue;
        }

        // Now that we have the correct window, get its service provider to access deeper shell interfaces.
        stage = 2; // Matched the launch window in the shell-windows collection.
        IServiceProvider* psp = nullptr;
        hr = pwba->QueryInterface(IID_IServiceProvider, (void**)&psp);
        pwba->Release();
        if (FAILED(hr))
            continue;

        // Use the service provider to get the IShellBrowser interface, which represents the browser pane.
        IShellBrowser* psb = nullptr;
        hr = psp->QueryService(SID_STopLevelBrowser, IID_IShellBrowser, (void**)&psb);
        psp->Release();
        if (FAILED(hr))
            continue;

        // Get the HWND of the shell browser's view. This corresponds to the active tab.
        stage = 3; // Got IShellBrowser.
        HWND hwndShellBrowser = nullptr;
        hr = psb->GetWindow(&hwndShellBrowser);
        // FIX: On modern Windows 11 Explorer, hwndActiveTab is a child of hwndShellBrowser
        // rather than being the same window. Accept both the direct equality case (older Explorer)
        // and the parent-child case (Windows 11 Explorer) to avoid skipping the correct window.
        if (FAILED(hr) || (hwndShellBrowser != hwndActiveTab && !IsChild(hwndShellBrowser, hwndActiveTab)))
        {
            psb->Release();
            continue;
        }

        // We've found the correct tab. Now get its IShellView to access the contents.
        stage = 4; // Tab-window identity check passed.
        IShellView* psv = nullptr;
        hr = psb->QueryActiveShellView(&psv);
        psb->Release();
        if (FAILED(hr))
            continue;

        // For more direct control over the view's items, query for the IFolderView interface.
        stage = 5; // Got IShellView.
        IFolderView* pfv = nullptr;
        hr = psv->QueryInterface(IID_IFolderView, (void**)&pfv);
        psv->Release();
        if (FAILED(hr))
            continue;

        // Retrieve all items in the folder view as a modern IShellItemArray.
        // FIX: SVGIO_FLAG_VIEWORDER is a modifier flag and must be combined with a base scope.
        // Using it alone results in undefined behavior and silent empty results on some Explorer views.
        stage = 6; // Got IFolderView.
        IShellItemArray* psia = nullptr;
        hr = pfv->Items(SVGIO_ALLVIEW | SVGIO_FLAG_VIEWORDER, IID_IShellItemArray, (void**)&psia);
        pfv->Release();
        if (FAILED(hr))
            continue;

        // Get the total number of items in the array.
        stage = 7; // Items() succeeded; anything past here means the view itself yielded nothing usable.
        DWORD count = 0;
        hr = psia->GetCount(&count);
        if (FAILED(hr))
        {
            psia->Release();
            continue;
        }

        // Iterate through the array of shell items.
        for (DWORD i = 0; i < count; i++)
        {
            // Extract the specific IShellItem at the current index.
            IShellItem* psi = nullptr;
            hr = psia->GetItemAt(i, &psi);
            if (FAILED(hr))
                continue; // Skip this specific item if extraction fails.

            PWSTR pszPath = nullptr;

            // Request the full file system path. The shell dynamically allocates memory for this string,
            // making it perfectly safe for paths exceeding MAX_PATH (260 characters).
            hr = psi->GetDisplayName(SIGDN_FILESYSPATH, &pszPath);
            if (SUCCEEDED(hr) && pszPath)
            {
                // Emplace the string directly into the vector to avoid unnecessary string copies.
                arr.emplace_back(pszPath);
                // Free the dynamically allocated string memory provided by the shell.
                CoTaskMemFree(pszPath);
            }
            psi->Release();
        }

        psia->Release();
        break; // Exit the loop since we've found and processed the active tab.
    }

    psw->Release();

    // FIX: Do not return hr here. After the loop, hr holds the result of the last
    // GetDisplayName() call, which may be a failure code from the final item even
    // if all other items succeeded. Return a result that reflects the overall outcome.
    //
    // On failure the stage counter says how far the best candidate window got:
    //   0 launch window has no Explorer tab child     4 QueryActiveShellView failed
    //   1 no shell window matched the launch HWND     5 IShellView -> IFolderView QI failed
    //   2 IShellBrowser service lookup failed         6 IFolderView::Items failed
    //   3 tab-window identity check rejected it       7 view returned 0 items, or none had a
    //                                                   filesystem path (search/library/virtual)
    return arr.empty() ? FLY_E_ENUM_STAGE(stage) : S_OK;
}