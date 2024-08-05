
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <iostream>

#include "ExplorerContextMenu.h"

#define SCRATCH_QCM_FIRST 1
#define SCRATCH_QCM_LAST  0x7FFF

IContextMenu2* ExplorerContextMenu::s_pICM2 = nullptr;
IContextMenu3* ExplorerContextMenu::s_pICM3 = nullptr;
bool ExplorerContextMenu::s_classRegistered = false;
const wchar_t ExplorerContextMenu::CLASS_NAME[] = L"HiddenWindowClass";

bool ExplorerContextMenu::ShowContextMenu(HINSTANCE appInstance, LPCWSTR filePath, int posX, int posY)
{
    bool retStatus = false;
    HWND hWnd = CreateHiddenWindow(appInstance);
    if (!hWnd)
    {
        std::cerr << "Failed to create hidden window." << '\n';
        return retStatus;
    }

    LPITEMIDLIST pidl = nullptr;
    HRESULT hr = SHParseDisplayName(filePath, nullptr, &pidl, 0, nullptr);
    if (FAILED(hr) || !pidl)
    {
        std::cerr << "Failed to parse display name." << '\n';
        DestroyWindow(hWnd);
        return retStatus;
    }

    IShellFolder* pParentFolder = nullptr;
    LPCITEMIDLIST pidlChild = nullptr;

    hr = SHBindToParent(pidl, IID_IShellFolder, reinterpret_cast<void**>(&pParentFolder), &pidlChild);
    if (FAILED(hr) || !pParentFolder || !pidlChild)
    {
        std::cerr << "Failed to bind to parent folder." << '\n';
        CoTaskMemFree((void*)pidl);
        DestroyWindow(hWnd);
        return retStatus;
    }

    IContextMenu* pContextMenu = nullptr;
    hr = pParentFolder->GetUIObjectOf(hWnd, 1, &pidlChild, IID_IContextMenu, nullptr, reinterpret_cast<void**>(&pContextMenu));
    if (FAILED(hr) || !pContextMenu)
    {
        std::cerr << "Failed to get IContextMenu." << '\n';
        pParentFolder->Release();
        CoTaskMemFree((void*)pidl);
        DestroyWindow(hWnd);
        return retStatus;
    }

    HMENU hMenu = CreatePopupMenu();
    if (!hMenu)
    {
        std::cerr << "Failed to create popup menu." << '\n';
        pContextMenu->Release();
        pParentFolder->Release();
        CoTaskMemFree((void*)pidl);
        DestroyWindow(hWnd);
        return retStatus;
    }

    hr = pContextMenu->QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE);
    if (SUCCEEDED(hr))
    {
        hr = pContextMenu->QueryInterface(IID_IContextMenu2, reinterpret_cast<void**>(&s_pICM2));
        if (SUCCEEDED(hr))
        {
            pContextMenu->QueryInterface(IID_IContextMenu3, reinterpret_cast<void**>(&s_pICM3));
        }

        UINT idCmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_VERPOSANIMATION, posX, posY, 0, hWnd, nullptr);
        retStatus = true;

        if (idCmd > 0)
        {
            POINT pt = { posX, posY };

            CMINVOKECOMMANDINFOEX info = {};
            info.cbSize = sizeof(info);
            info.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE; // remember the point for "properties"
            if (GetKeyState(VK_CONTROL) < 0)
            {
                // send key states (for delete command)
                info.fMask |= CMIC_MASK_CONTROL_DOWN;
            }
            if (GetKeyState(VK_SHIFT) < 0)
            {
                info.fMask |= CMIC_MASK_SHIFT_DOWN;
            }
            info.hwnd = hWnd;
            info.lpVerb = MAKEINTRESOURCEA(idCmd - SCRATCH_QCM_FIRST);
            info.lpVerbW = MAKEINTRESOURCEW(idCmd - SCRATCH_QCM_FIRST);
            info.nShow = SW_SHOWNORMAL;
            info.ptInvoke = pt; // pass the point to "properties"
            hr = pContextMenu->InvokeCommand((LPCMINVOKECOMMANDINFO)&info);
            if (FAILED(hr))
            {
                std::cerr << "Failed to invoke command." << '\n';
            }
        }

        CleanUp();
    }
    else
    {
        std::cerr << "Failed to query context menu." << '\n';
    }

    DestroyMenu(hMenu);
    pContextMenu->Release();
    pParentFolder->Release();
    CoTaskMemFree((void*)pidl);
    DestroyWindow(hWnd);
    return retStatus;
}

HWND ExplorerContextMenu::CreateHiddenWindow(HINSTANCE appInstance)
{
    if (!s_classRegistered)
    {
        WNDCLASS wc = { };
        wc.lpfnWndProc = WndProc;
        wc.hInstance = appInstance;
        wc.lpszClassName = CLASS_NAME;

        if (!RegisterClass(&wc))
        {
            std::cerr << "Failed to register window class." << '\n';
            return nullptr;
        }

        s_classRegistered = true;
    }

    return CreateWindowEx(
        0,
        CLASS_NAME,
        L"Hidden Window",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT,
        nullptr,
        nullptr,
        appInstance,
        this
    );
}

LRESULT CALLBACK ExplorerContextMenu::WndProc(HWND hWnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (s_pICM3)
    {
        LRESULT lResult = 0;
        if (s_pICM3->HandleMenuMsg2(message, wParam, lParam, &lResult) == S_OK)
        {
            return lResult;
        }
    }
    else if (s_pICM2)
    {
        if (s_pICM2->HandleMenuMsg(message, wParam, lParam) == S_OK)
        {
            return 0;
        }
    }

    return DefWindowProc(hWnd, message, wParam, lParam);
}

void ExplorerContextMenu::CleanUp()
{
    if (s_pICM3)
    {
        s_pICM3->Release();
        s_pICM3 = nullptr;
    }

    if (s_pICM2)
    {
        s_pICM2->Release();
        s_pICM2 = nullptr;
    }
}