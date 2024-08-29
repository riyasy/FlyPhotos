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

ShellUtility::ShellUtility()
{
}

ShellUtility::~ShellUtility()
{
}

void ShellUtility::SayThis(const wchar_t* phrase)
{
	MessageBox(nullptr, phrase, L"Sample Title", MB_OK);
}

HRESULT ShellUtility::GetFileListFromExplorerWindow(vector<wstring>& arr)
{
    TCHAR g_szItem[MAX_PATH];
    g_szItem[0] = TEXT('\0');

    // Get the handle to the foreground window (the currently active window).
    HWND hwndFind = GetForegroundWindow();

    // Try to find the active tab window in the foreground window.
    // First, look for "ShellTabWindowClass", then "TabWindowClass".
    HWND hwndActiveTab = FindWindowEx(hwndFind, nullptr, L"ShellTabWindowClass", nullptr);
    if (hwndActiveTab == nullptr)
    {
        hwndActiveTab = FindWindowEx(hwndFind, nullptr, L"TabWindowClass", nullptr);
    }

    // If no active tab was found, return an error code.
    if (hwndActiveTab == nullptr)
    {
        return E_FAIL; // No active tab found
    }

    // Create an instance of IShellWindows to enumerate all open shell windows.
    IShellWindows* psw = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_ALL, IID_IShellWindows, (void**)&psw);
    if (FAILED(hr))
    {
        return hr; // Failed to create ShellWindows instance
    }

    VARIANT v;
    V_VT(&v) = VT_I4;
    IDispatch* pdisp = nullptr;

    // Iterate through all shell windows to find the one matching the foreground window.
    for (V_I4(&v) = 0; psw->Item(v, &pdisp) == S_OK; V_I4(&v)++)
    {
        IWebBrowserApp* pwba = nullptr;
        hr = pdisp->QueryInterface(IID_IWebBrowserApp, (void**)&pwba);
        pdisp->Release(); // Release IDispatch as it's no longer needed.
        if (FAILED(hr))
        {
            continue; // Skip to the next shell window if QueryInterface fails.
        }

        // Get the window handle of the current IWebBrowserApp.
        HWND hwndWBA;
        hr = pwba->get_HWND((LONG_PTR*)&hwndWBA);
        if (FAILED(hr) || hwndWBA != hwndFind)
        {
            pwba->Release(); // Release IWebBrowserApp if not the target window.
            continue; // Skip to the next shell window.
        }

        // Query for the IServiceProvider interface.
        IServiceProvider* psp = nullptr;
        hr = pwba->QueryInterface(IID_IServiceProvider, (void**)&psp);
        pwba->Release(); // Release IWebBrowserApp as it's no longer needed.
        if (FAILED(hr))
        {
            continue; // Skip to the next shell window if IServiceProvider query fails.
        }

        // Use IServiceProvider to get the IShellBrowser interface.
        IShellBrowser* psb = nullptr;
        hr = psp->QueryService(SID_STopLevelBrowser, IID_IShellBrowser, (void**)&psb);
        psp->Release(); // Release IServiceProvider as it's no longer needed.
        if (FAILED(hr))
        {
            continue; // Skip to the next shell window if IShellBrowser query fails.
        }

        // Get the window handle of the shell browser.
        HWND hwndShellBrowser;
        hr = psb->GetWindow(&hwndShellBrowser);
        if (FAILED(hr) || hwndShellBrowser != hwndActiveTab)
        {
            psb->Release(); // Release IShellBrowser if not the active tab.
            continue; // Skip to the next shell window.
        }

        // Retrieve the active shell view from the shell browser.
        IShellView* psv = nullptr;
        hr = psb->QueryActiveShellView(&psv);
        psb->Release(); // Release IShellBrowser as it's no longer needed.
        if (FAILED(hr))
        {
            continue; // Skip to the next shell window if IShellView query fails.
        }

        // Query for the IFolderView interface from the active shell view.
        IFolderView* pfv = nullptr;
        hr = psv->QueryInterface(IID_IFolderView, (void**)&pfv);
        psv->Release(); // Release IShellView as it's no longer needed.
        if (FAILED(hr))
        {
            continue; // Skip to the next shell window if IFolderView query fails.
        }

        // Get the IShellFolder interface from the folder view.
        IShellFolder* psf = nullptr;
        hr = pfv->GetFolder(IID_IShellFolder, (void**)&psf);
        if (FAILED(hr))
        {
            pfv->Release(); // Release IFolderView if IShellFolder query fails.
            continue; // Skip to the next shell window.
        }

        // Enumerate the items in the folder view.
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

        // Iterate through the items and get their display names.
        while (pEnum->Next(1, &pidl, &fetched) == S_OK && fetched)
        {
            hr = psf->GetDisplayNameOf(pidl, SHGDN_FORPARSING, &str);
            if (SUCCEEDED(hr))
            {
                // Convert STRRET to a string and add it to the output array.
                StrRetToBuf(&str, pidl, g_szItem, MAX_PATH);
                arr.push_back(g_szItem);
            }
            CoTaskMemFree(pidl); // Free the PIDL after processing.
        }

        pEnum->Release(); // Release the item enumerator.
        psf->Release(); // Release IShellFolder.
        break; // Exit the loop since we've found the active tab.
    }

    psw->Release(); // Release IShellWindows.
    return hr; // Return the result.
}



