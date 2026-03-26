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

/**
 * @brief Retrieves the full paths of all visible items in the currently active File Explorer window.
 * @details This function uses a series of COM interfaces to navigate from the top-level
 *          shell windows down to the specific folder view of the active tab. It utilizes
 *          the modern IShellItem API (Windows Vista+) to robustly enumerate items,
 *          natively supporting long file paths without fixed buffer limitations.
 */
HRESULT ShellUtility::GetFileListFromExplorerWindow(std::vector<std::wstring>& arr)
{
    // Get the handle to the foreground window (the currently active window).
    HWND hwndFind = GetForegroundWindow();

    // Try to find the active tab window within the foreground Explorer window.
    // Modern Explorer (Win 10/11) uses "ShellTabWindowClass". Older versions might use "TabWindowClass".
    HWND hwndActiveTab = FindWindowEx(hwndFind, nullptr, L"ShellTabWindowClass", nullptr);
    if (hwndActiveTab == nullptr)
        hwndActiveTab = FindWindowEx(hwndFind, nullptr, L"TabWindowClass", nullptr);

    // If no active tab window was found, we cannot proceed.
    if (hwndActiveTab == nullptr)
        return E_FAIL; // No active tab found

    // Create an instance of IShellWindows to enumerate all open shell (Explorer) windows.
    IShellWindows* psw = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_ALL, IID_IShellWindows, (void**)&psw);
    if (FAILED(hr))
        return hr; // Failed to create ShellWindows instance

    // Properly initialize the VARIANT used to index into the shell windows collection.
    VARIANT v;
    VariantInit(&v);
    V_VT(&v) = VT_I4;

    IDispatch* pdisp = nullptr;

    // Iterate through all shell windows to find the one matching the foreground window handle.
    for (V_I4(&v) = 0; psw->Item(v, &pdisp) == S_OK; V_I4(&v)++)
    {
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
        IShellView* psv = nullptr;
        hr = psb->QueryActiveShellView(&psv);
        psb->Release();
        if (FAILED(hr))
            continue;

        // For more direct control over the view's items, query for the IFolderView interface.
        IFolderView* pfv = nullptr;
        hr = psv->QueryInterface(IID_IFolderView, (void**)&pfv);
        psv->Release();
        if (FAILED(hr))
            continue;

        // Retrieve all items in the folder view as a modern IShellItemArray.
        // FIX: SVGIO_FLAG_VIEWORDER is a modifier flag and must be combined with a base scope.
        // Using it alone results in undefined behavior and silent empty results on some Explorer views.
        IShellItemArray* psia = nullptr;
        hr = pfv->Items(SVGIO_ALLVIEW | SVGIO_FLAG_VIEWORDER, IID_IShellItemArray, (void**)&psia);
        pfv->Release();
        if (FAILED(hr))
            continue;

        // Get the total number of items in the array.
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
    return arr.empty() ? E_FAIL : S_OK;
}