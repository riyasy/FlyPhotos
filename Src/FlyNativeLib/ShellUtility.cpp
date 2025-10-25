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
 *          shell windows down to the specific folder view of the active tab, and then
 *          enumerates the items within that view.
 */
HRESULT ShellUtility::GetFileListFromExplorerWindow(vector<wstring>& arr)
{
    // A buffer to hold the file path string retrieved for each item.
    TCHAR g_szItem[MAX_PATH];
    g_szItem[0] = TEXT('\0');

    // Get the handle to the foreground window (the currently active window).
    HWND hwndFind = GetForegroundWindow();

    // Try to find the active tab window within the foreground Explorer window.
    // Modern Explorer (Win 10/11) uses "ShellTabWindowClass". Older versions might use "TabWindowClass".
    HWND hwndActiveTab = FindWindowEx(hwndFind, nullptr, L"ShellTabWindowClass", nullptr);
    if (hwndActiveTab == nullptr)
    {
        hwndActiveTab = FindWindowEx(hwndFind, nullptr, L"TabWindowClass", nullptr);
    }

    // If no active tab window was found, we cannot proceed.
    if (hwndActiveTab == nullptr)
    {
        return E_FAIL; // No active tab found
    }

    // Create an instance of IShellWindows to enumerate all open shell (Explorer) windows.
    IShellWindows* psw = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_ALL, IID_IShellWindows, (void**)&psw);
    if (FAILED(hr))
    {
        return hr; // Failed to create ShellWindows instance
    }

    VARIANT v;
    V_VT(&v) = VT_I4;
    IDispatch* pdisp = nullptr;

    // Iterate through all shell windows to find the one matching the foreground window handle.
    for (V_I4(&v) = 0; psw->Item(v, &pdisp) == S_OK; V_I4(&v)++)
    {
        // Each shell window is an IDispatch; query it for the IWebBrowserApp interface.
        IWebBrowserApp* pwba = nullptr;
        hr = pdisp->QueryInterface(IID_IWebBrowserApp, (void**)&pwba);
        pdisp->Release(); // Release IDispatch as it's no longer needed.
        if (FAILED(hr))
        {
            continue; // Skip to the next shell window if QueryInterface fails.
        }

        // Get the top-level window handle (HWND) of the current IWebBrowserApp.
        HWND hwndWBA;
        hr = pwba->get_HWND((LONG_PTR*)&hwndWBA);
        // If this isn't our target window, release and continue the loop.
        if (FAILED(hr) || hwndWBA != hwndFind)
        {
            pwba->Release(); // Release IWebBrowserApp if not the target window.
            continue; // Skip to the next shell window.
        }

        // Now that we have the correct window, get its service provider to access deeper shell interfaces.
        IServiceProvider* psp = nullptr;
        hr = pwba->QueryInterface(IID_IServiceProvider, (void**)&psp);
        pwba->Release(); // Release IWebBrowserApp as it's no longer needed.
        if (FAILED(hr))
        {
            continue; // Skip to the next shell window if IServiceProvider query fails.
        }

        // Use the service provider to get the IShellBrowser interface, which represents the browser pane.
        IShellBrowser* psb = nullptr;
        hr = psp->QueryService(SID_STopLevelBrowser, IID_IShellBrowser, (void**)&psb);
        psp->Release(); // Release IServiceProvider as it's no longer needed.
        if (FAILED(hr))
        {
            continue; // Skip to the next shell window if IShellBrowser query fails.
        }

        // Get the HWND of the shell browser's view. This corresponds to the active tab.
        HWND hwndShellBrowser;
        hr = psb->GetWindow(&hwndShellBrowser);
        // If this handle doesn't match the active tab handle we found earlier, it's not the right view.
        if (FAILED(hr) || hwndShellBrowser != hwndActiveTab)
        {
            psb->Release(); // Release IShellBrowser if not the active tab.
            continue; // Skip to the next shell window.
        }

        // We've found the correct tab. Now get its IShellView to access the contents.
        IShellView* psv = nullptr;
        hr = psb->QueryActiveShellView(&psv);
        psb->Release(); // Release IShellBrowser as it's no longer needed.
        if (FAILED(hr))
        {
            continue; // Skip to the next shell window if IShellView query fails.
        }

        // For more direct control over the view's items, query for the IFolderView interface.
        IFolderView* pfv = nullptr;
        hr = psv->QueryInterface(IID_IFolderView, (void**)&pfv);
        psv->Release(); // Release IShellView as it's no longer needed.
        if (FAILED(hr))
        {
            continue; // Skip to the next shell window if IFolderView query fails.
        }

        // Get the IShellFolder for the directory currently displayed in the folder view.
        IShellFolder* psf = nullptr;
        hr = pfv->GetFolder(IID_IShellFolder, (void**)&psf);
        if (FAILED(hr))
        {
            pfv->Release(); // Release IFolderView if IShellFolder query fails.
            continue; // Skip to the next shell window.
        }

        // Get an enumerator (IEnumIDList) for all items in the folder view.
        IEnumIDList* pEnum = nullptr;
        hr = pfv->Items(SVGIO_FLAG_VIEWORDER, IID_IEnumIDList, (LPVOID*)&pEnum);
        pfv->Release(); // Release IFolderView as it's no longer needed.
        if (FAILED(hr))
        {
            psf->Release(); // Release IShellFolder if enumeration fails.
            continue; // Skip to the next shell window.
        }

        LPITEMIDLIST pidl;
        ULONG fetched = 0;
        STRRET str;

        // Iterate through all items (PIDLs) returned by the enumerator.
        while (pEnum->Next(1, &pidl, &fetched) == S_OK && fetched)
        {
            // For each item, get its display name as a full parsing path.
            hr = psf->GetDisplayNameOf(pidl, SHGDN_FORPARSING, &str);
            if (SUCCEEDED(hr))
            {
                // Convert the shell's STRRET structure to a standard wide string.
                StrRetToBuf(&str, pidl, g_szItem, MAX_PATH);
                // Add the resulting path to the output vector.
                arr.push_back(g_szItem);
            }
            CoTaskMemFree(pidl); // Free the PIDL memory allocated by the shell.
        }

        pEnum->Release(); // Release the item enumerator.
        psf->Release(); // Release IShellFolder.
        break; // Exit the loop since we've found and processed the active tab.
    }

    psw->Release(); // Release IShellWindows.
    return hr; // Return the result.
}