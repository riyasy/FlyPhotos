
#include "pch.h"
#include "ExplorerContextMenu.h"
#include <shlobj.h>
#include <iostream>


bool ExplorerContextMenu::s_classRegistered = false;
const wchar_t ExplorerContextMenu::CLASS_NAME[] = L"FlyPhotosHiddenMenuWindow";
#define SCRATCH_QCM_FIRST 1

bool ExplorerContextMenu::ShowContextMenu(HINSTANCE appInstance, LPCWSTR filePath, int posX, int posY)
{
    LPITEMIDLIST pidl = nullptr;
    HRESULT hr = SHParseDisplayName(filePath, nullptr, &pidl, 0, nullptr);
    if (FAILED(hr)) return false;

    IShellFolder* pParentFolder = nullptr;
    LPCITEMIDLIST pidlChild = nullptr;
    hr = SHBindToParent(pidl, IID_IShellFolder, reinterpret_cast<void**>(&pParentFolder), &pidlChild);
    if (FAILED(hr)) {
        CoTaskMemFree(pidl);
        return false;
    }

    MenuContext* pMenuContext = new MenuContext();
    HWND hWnd = CreateHiddenWindow(appInstance, pMenuContext);
    if (!hWnd) {
        delete pMenuContext;
        pParentFolder->Release();
        CoTaskMemFree(pidl);
        return false;
    }

    // Get IContextMenu directly
    IContextMenu* pContextMenu = nullptr;
    hr = pParentFolder->GetUIObjectOf(hWnd, 1, &pidlChild,
        IID_IContextMenu, nullptr, reinterpret_cast<void**>(&pContextMenu));
    if (FAILED(hr)) {
        DestroyWindow(hWnd);
        delete pMenuContext;
        pParentFolder->Release();
        CoTaskMemFree(pidl);
        return false;
    }

    // Store v2/v3 for WndProc
    pContextMenu->QueryInterface(IID_IContextMenu2, (void**)&pMenuContext->pICM2);
    pContextMenu->QueryInterface(IID_IContextMenu3, (void**)&pMenuContext->pICM3);

    HMENU hMenu = CreatePopupMenu();
    if (!hMenu) {
        pContextMenu->Release();
        DestroyWindow(hWnd);
        delete pMenuContext;
        pParentFolder->Release();
        CoTaskMemFree(pidl);
        return false;
    }

    // Safe flags first — can test adding CMF_EXTENDEDVERBS later
    UINT uFlags = CMF_NORMAL | CMF_EXPLORE;
    hr = pContextMenu->QueryContextMenu(hMenu, 0, SCRATCH_QCM_FIRST, 0x7FFF, uFlags);

    bool retStatus = false;
    if (SUCCEEDED(hr)) {
        UINT idCmd = TrackPopupMenu(hMenu,
            TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_VERPOSANIMATION,
            posX, posY, 0, hWnd, nullptr);

        if (idCmd != 0 && idCmd >= SCRATCH_QCM_FIRST) {
            CMINVOKECOMMANDINFOEX info = { sizeof(info) };
            // info.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
            info.fMask = CMIC_MASK_PTINVOKE;
            if (GetKeyState(VK_CONTROL) < 0) info.fMask |= CMIC_MASK_CONTROL_DOWN;
            if (GetKeyState(VK_SHIFT) < 0)   info.fMask |= CMIC_MASK_SHIFT_DOWN;
            info.hwnd = hWnd;
            info.lpVerb = MAKEINTRESOURCEA(idCmd - SCRATCH_QCM_FIRST);
            info.lpVerbW = MAKEINTRESOURCEW(idCmd - SCRATCH_QCM_FIRST);
            info.nShow = SW_SHOWNORMAL;
            info.ptInvoke = { posX, posY };

            pContextMenu->InvokeCommand((LPCMINVOKECOMMANDINFO)&info);
        }
        retStatus = true;
    }

    // Cleanup order
    DestroyMenu(hMenu);
    pContextMenu->Release();
    pParentFolder->Release();
    CoTaskMemFree(pidl);

    // Now destroy the hidden window (which will release pICM2/3 in WndProc)
    DestroyWindow(hWnd);

    // After the window is fully gone, free our struct
    delete pMenuContext;

    return retStatus;
}

HWND ExplorerContextMenu::CreateHiddenWindow(HINSTANCE appInstance, MenuContext* pContext)
{
    if (!s_classRegistered) {
        WNDCLASS wc = {};
        wc.lpfnWndProc = WndProc;
        wc.hInstance = appInstance;
        wc.lpszClassName = CLASS_NAME;
        if (!RegisterClass(&wc)) {
            std::cerr << "Failed to register window class.\n";
            return nullptr;
        }
        s_classRegistered = true;
    }
    return CreateWindowEx(0, CLASS_NAME, L"Hidden Menu Handler", 0, 0, 0, 0, 0,
        HWND_MESSAGE, nullptr, appInstance, pContext);
}

LRESULT CALLBACK ExplorerContextMenu::WndProc(HWND hWnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    MenuContext* pContext = nullptr;
    if (message == WM_CREATE) {
        CREATESTRUCT* pCreate = reinterpret_cast<CREATESTRUCT*>(lParam);
        pContext = reinterpret_cast<MenuContext*>(pCreate->lpCreateParams);
        SetWindowLongPtr(hWnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(pContext));
        return 0;
    }

    pContext = reinterpret_cast<MenuContext*>(GetWindowLongPtr(hWnd, GWLP_USERDATA));

    if (pContext) {
        if (pContext->pICM3) {
            LRESULT lResult;
            if (pContext->pICM3->HandleMenuMsg2(message, wParam, lParam, &lResult) == S_OK)
                return lResult;
        }
        else if (pContext->pICM2) {
            if (pContext->pICM2->HandleMenuMsg(message, wParam, lParam) == S_OK)
                return 0;
        }
    }

    if (message == WM_NCDESTROY) {
        // Release COM objects here, but DO NOT delete pContext
        if (pContext) {
            if (pContext->pICM3) { pContext->pICM3->Release(); pContext->pICM3 = nullptr; }
            if (pContext->pICM2) { pContext->pICM2->Release(); pContext->pICM2 = nullptr; }
            SetWindowLongPtr(hWnd, GWLP_USERDATA, 0);
        }
        return 0;
    }

    return DefWindowProc(hWnd, message, wParam, lParam);
}
