#include "pch.h"
#include "ShellContextMenu.h"
#include <shlwapi.h>
#include <string>
#include <iostream>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "ole32.lib")

// A unique name for our hidden window class
const WCHAR WND_CLASS_NAME[] = L"ShellContextMenuHelperWnd";

ShellContextMenu::ShellContextMenu()
    : m_isInitialized(false), m_hMessageWnd(NULL), m_pContextMenu(nullptr),
    m_pContextMenu2(nullptr), m_pContextMenu3(nullptr), m_pParentFolder(nullptr)
{
    // The constructor is kept simple to prevent failures. All complex
    // initialization is moved to the Init() method.
}

ShellContextMenu::~ShellContextMenu() {
    // Release all resources.
    ReleaseAll(); // Release COM objects and PIDLs first.

    if (m_hMessageWnd) {
        DestroyWindow(m_hMessageWnd);
    }
    UnregisterClass(WND_CLASS_NAME, GetModuleHandle(NULL));

    // Only uninitialize OLE if it was successfully initialized.
    if (m_isInitialized) {
        OleFlushClipboard(); // Make clipboard data persistent after app closes.
        OleUninitialize();
    }
}

SCM_RESULT ShellContextMenu::Init() {
    if (m_isInitialized) {
        return SCM_RESULT::Success;
    }

    // OLE initialization is required for clipboard operations (Cut, Copy).
    if (FAILED(OleInitialize(NULL))) {
        return SCM_RESULT::OleInitializeFailed;
    }

    // Create the hidden window required for message handling.
    SCM_RESULT result = CreateMessageWindow();
    if (result != SCM_RESULT::Success) {
        OleUninitialize(); // Clean up OLE if window creation failed.
        return result;
    }

    m_isInitialized = true;
    return SCM_RESULT::Success;
}

SCM_RESULT ShellContextMenu::CreateMessageWindow() {
    HINSTANCE hInstance = GetModuleHandle(NULL);
    WNDCLASS wc = {};
    wc.lpfnWndProc = ShellContextMenu::WndProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = WND_CLASS_NAME;

    WNDCLASS existing_wc = {};
    if (!GetClassInfo(hInstance, WND_CLASS_NAME, &existing_wc)) {
        if (!RegisterClass(&wc)) {
            return SCM_RESULT::WindowRegistrationFailed;
        }
    }

    m_hMessageWnd = CreateWindowEx(0, WND_CLASS_NAME, L"CtxMenuHelper", 0, 0, 0, 0, 0,
        HWND_MESSAGE, NULL, hInstance, nullptr);

    if (!m_hMessageWnd) {
        return SCM_RESULT::WindowCreationFailed;
    }

    SetWindowLongPtr(m_hMessageWnd, GWLP_USERDATA, (LONG_PTR)this);
    return SCM_RESULT::Success;
}

SCM_RESULT ShellContextMenu::ShowContextMenu(HWND owner, const std::vector<std::wstring>& files, POINT pt) {
    ReleaseAll(); // Ensure a clean state for this operation.

    SCM_RESULT result = GetParentAndPIDLs(files);
    if (result != SCM_RESULT::Success) {
        ReleaseAll();
        return result;
    }

    std::vector<LPCITEMIDLIST> const_pidls(m_pidls.begin(), m_pidls.end());
    result = GetContextMenuInterfaces(m_pParentFolder, const_pidls);
    if (result != SCM_RESULT::Success) {
        ReleaseAll();
        return result;
    }

    HMENU hMenu = CreatePopupMenu();
    if (!hMenu) {
        ReleaseAll();
        return SCM_RESULT::MenuCreationFailed;
    }

    UINT uFlags = CMF_NORMAL | CMF_EXPLORE;
    if (GetKeyState(VK_SHIFT) < 0) {
        uFlags |= CMF_EXTENDEDVERBS;
    }

    if (FAILED(m_pContextMenu->QueryContextMenu(hMenu, 0, 1, 0x7FFF, uFlags))) {
        DestroyMenu(hMenu);
        ReleaseAll();
        return SCM_RESULT::QueryContextMenuFailed;
    }

    UINT iCmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD, pt.x, pt.y, m_hMessageWnd, NULL);
    if (iCmd > 0) {
        InvokeCommand(iCmd - 1, pt);
    }

    DestroyMenu(hMenu);
    ReleaseAll(); // Final cleanup for this operation.
    return SCM_RESULT::Success;
}

void ShellContextMenu::ReleaseAll() {
    if (m_pContextMenu) { m_pContextMenu->Release(); m_pContextMenu = nullptr; }
    if (m_pContextMenu2) { m_pContextMenu2->Release(); m_pContextMenu2 = nullptr; }
    if (m_pContextMenu3) { m_pContextMenu3->Release(); m_pContextMenu3 = nullptr; }
    if (m_pParentFolder) { m_pParentFolder->Release(); m_pParentFolder = nullptr; }
    for (LPITEMIDLIST pidl : m_pidls) {
        CoTaskMemFree(pidl);
    }
    m_pidls.clear();
    m_parentFolderStr.clear();
}

SCM_RESULT ShellContextMenu::GetParentAndPIDLs(const std::vector<std::wstring>& files) {
    if (files.empty()) return SCM_RESULT::InvalidInput;

    WCHAR szParentPath[MAX_PATH];
    wcscpy_s(szParentPath, files[0].c_str());
    PathRemoveFileSpecW(szParentPath);
    m_parentFolderStr = szParentPath;

    IShellFolder* pDesktopFolder = nullptr;
    if (FAILED(SHGetDesktopFolder(&pDesktopFolder))) return SCM_RESULT::GetDesktopFolderFailed;

    LPITEMIDLIST pParentPidl = nullptr;
    HRESULT hr = pDesktopFolder->ParseDisplayName(NULL, NULL, szParentPath, NULL, &pParentPidl, NULL);
    if (FAILED(hr)) {
        pDesktopFolder->Release();
        return SCM_RESULT::GetParentFolderFailed;
    }

    hr = pDesktopFolder->BindToObject(pParentPidl, NULL, IID_IShellFolder, (void**)&m_pParentFolder);
    CoTaskMemFree(pParentPidl);
    pDesktopFolder->Release();
    if (FAILED(hr)) return SCM_RESULT::GetParentFolderFailed;

    for (const auto& file : files) {
        LPITEMIDLIST pFilePidl = nullptr;
        std::wstring fileName = PathFindFileNameW(file.c_str());
        if (FAILED(m_pParentFolder->ParseDisplayName(NULL, NULL, (LPWSTR)fileName.c_str(), NULL, &pFilePidl, NULL))) {
            return SCM_RESULT::PidlCreationFialed;
        }
        m_pidls.push_back(pFilePidl);
    }
    return SCM_RESULT::Success;
}

SCM_RESULT ShellContextMenu::GetContextMenuInterfaces(IShellFolder* pParentFolder, const std::vector<LPCITEMIDLIST>& pidls) {
    if (!pParentFolder || pidls.empty()) return SCM_RESULT::InvalidInput;

    LPCITEMIDLIST* nonConstPidls = const_cast<LPCITEMIDLIST*>(pidls.data());
    if (FAILED(pParentFolder->GetUIObjectOf(m_hMessageWnd, static_cast<UINT>(pidls.size()), nonConstPidls, IID_IContextMenu, NULL, (void**)&m_pContextMenu))) {
        return SCM_RESULT::GetContextMenuInterfacesFailed;
    }
    m_pContextMenu->QueryInterface(IID_IContextMenu2, (void**)&m_pContextMenu2);
    m_pContextMenu->QueryInterface(IID_IContextMenu3, (void**)&m_pContextMenu3);
    return SCM_RESULT::Success;
}

void ShellContextMenu::InvokeCommand(UINT iCmd, POINT pt) {
    CMINVOKECOMMANDINFOEX cmi = { sizeof(cmi) };
    cmi.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
    if (GetKeyState(VK_CONTROL) < 0) cmi.fMask |= CMIC_MASK_CONTROL_DOWN;
    if (GetKeyState(VK_SHIFT) < 0) cmi.fMask |= CMIC_MASK_SHIFT_DOWN;
    cmi.hwnd = m_hMessageWnd;
    cmi.lpVerb = MAKEINTRESOURCEA(iCmd);
    cmi.lpVerbW = MAKEINTRESOURCEW(iCmd);
    cmi.lpDirectoryW = m_parentFolderStr.c_str();
    cmi.nShow = SW_SHOWNORMAL;
    cmi.ptInvoke = pt;
    m_pContextMenu->InvokeCommand((LPCMINVOKECOMMANDINFO)&cmi);
}

LRESULT CALLBACK ShellContextMenu::WndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
    ShellContextMenu* pThis = (ShellContextMenu*)GetWindowLongPtr(hWnd, GWLP_USERDATA);
    if (pThis) {
        return pThis->MessageHandler(uMsg, wParam, lParam);
    }
    return DefWindowProc(hWnd, uMsg, wParam, lParam);
}

LRESULT ShellContextMenu::MessageHandler(UINT uMsg, WPARAM wParam, LPARAM lParam) {
    if (m_pContextMenu2 && (uMsg == WM_INITMENUPOPUP || uMsg == WM_DRAWITEM || uMsg == WM_MEASUREITEM)) {
        if (SUCCEEDED(m_pContextMenu2->HandleMenuMsg(uMsg, wParam, lParam))) {
            return 0;
        }
    }
    if (m_pContextMenu3 && uMsg == WM_MENUCHAR) {
        LRESULT result = 0;
        if (SUCCEEDED(m_pContextMenu3->HandleMenuMsg2(uMsg, wParam, lParam, &result))) {
            return result;
        }
    }
    return DefWindowProc(m_hMessageWnd, uMsg, wParam, lParam);
}