/**
 * @file ShellContextMenu.cpp
 * @brief Implements the ShellContextMenu class for displaying the native shell context menu.
 */

#include "pch.h"
#include "ShellContextMenu.h"
#include <shlwapi.h>
#include <string>
#include <iostream>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "ole32.lib")

 // A unique name for our hidden message-handling window class.
const WCHAR WND_CLASS_NAME[] = L"ShellContextMenuHelperWnd";

/**
 * @brief Constructs a ShellContextMenu object and initializes member variables to a null state.
 */
ShellContextMenu::ShellContextMenu()
    : m_isInitialized(false), m_hMessageWnd(NULL), m_pContextMenu(nullptr),
    m_pContextMenu2(nullptr), m_pContextMenu3(nullptr), m_pParentFolder(nullptr)
{
    // The constructor is kept simple to prevent failures. All complex
    // initialization that can fail is moved to the Init() method.
}

/**
 * @brief Destructor that ensures all acquired resources are released.
 */
ShellContextMenu::~ShellContextMenu() {
    // Release all resources.
    ReleaseAll(); // Release COM objects and PIDLs first.

    // Destroy the hidden window if it was created.
    if (m_hMessageWnd) {
        DestroyWindow(m_hMessageWnd);
    }
    // Unregister the window class to clean up.
    UnregisterClass(WND_CLASS_NAME, GetModuleHandle(NULL));

    // Only uninitialize OLE if it was successfully initialized.
    if (m_isInitialized) {
        OleFlushClipboard(); // Make clipboard data persistent after app closes.
        OleUninitialize();
    }
}

/**
 * @brief Performs necessary one-time initialization for the class instance.
 * @details Initializes OLE/COM and creates a hidden window for message handling.
 *          This must be called before ShowContextMenu.
 */
SCM_RESULT ShellContextMenu::Init() {
    if (m_isInitialized) {
        return SCM_RESULT::Success;
    }

    // OLE initialization is required for clipboard operations (Cut, Copy) and shell interfaces.
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

/**
 * @brief Creates a hidden, message-only window to process messages for the context menu.
 * @details Some shell extensions require a window handle to process messages for
 *          owner-drawn menu items (via IContextMenu2/3).
 */
SCM_RESULT ShellContextMenu::CreateMessageWindow() {
    HINSTANCE hInstance = GetModuleHandle(NULL);
    WNDCLASS wc = {};
    wc.lpfnWndProc = ShellContextMenu::WndProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = WND_CLASS_NAME;

    // Register the window class only if it doesn't already exist.
    WNDCLASS existing_wc = {};
    if (!GetClassInfo(hInstance, WND_CLASS_NAME, &existing_wc)) {
        if (!RegisterClass(&wc)) {
            return SCM_RESULT::WindowRegistrationFailed;
        }
    }

    // Create an invisible, message-only window.
    m_hMessageWnd = CreateWindowEx(0, WND_CLASS_NAME, L"CtxMenuHelper", 0, 0, 0, 0, 0,
        HWND_MESSAGE, NULL, hInstance, nullptr);

    if (!m_hMessageWnd) {
        return SCM_RESULT::WindowCreationFailed;
    }

    // Store a pointer to this class instance in the window's user data,
    // so the static WndProc can route messages to the correct instance.
    SetWindowLongPtr(m_hMessageWnd, GWLP_USERDATA, (LONG_PTR)this);
    return SCM_RESULT::Success;
}

/**
 * @brief Displays the shell context menu for a given set of files at a specific point.
 * @details This is the main operational method of the class. It orchestrates getting the
 *          shell interfaces, building the menu, showing it, and invoking the selected command.
 */
SCM_RESULT ShellContextMenu::ShowContextMenu(HWND owner, const std::vector<std::wstring>& files, POINT pt) {
    ReleaseAll(); // Ensure a clean state for this operation.

    // Step 1: Get the parent IShellFolder and the PIDLs for the specified files.
    SCM_RESULT result = GetParentAndPIDLs(files);
    if (result != SCM_RESULT::Success) {
        ReleaseAll();
        return result;
    }

    // Step 2: Get the IContextMenu interfaces for the given items.
    std::vector<LPCITEMIDLIST> const_pidls(m_pidls.begin(), m_pidls.end());
    result = GetContextMenuInterfaces(m_pParentFolder, const_pidls);
    if (result != SCM_RESULT::Success) {
        ReleaseAll();
        return result;
    }

    // Step 3: Create a popup menu handle to be populated by the shell.
    HMENU hMenu = CreatePopupMenu();
    if (!hMenu) {
        ReleaseAll();
        return SCM_RESULT::MenuCreationFailed;
    }

    // Step 4: Ask the shell to populate our menu with items.
    // Check if the Shift key is pressed to show extended verbs.
    UINT uFlags = CMF_NORMAL | CMF_EXPLORE;
    if (GetKeyState(VK_SHIFT) < 0) {
        uFlags |= CMF_EXTENDEDVERBS;
    }

    if (FAILED(m_pContextMenu->QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, uFlags))) {
        DestroyMenu(hMenu);
        ReleaseAll();
        return SCM_RESULT::QueryContextMenuFailed;
    }

    // Step 5: (Optional) Customize the menu by removing unwanted items.
    RemoveMenuItemsByVerb(hMenu, { L"delete", L"cut" });

    // Step 6: Display the menu and get the user's selection.
    // The menu messages will be sent to our hidden window.
    UINT iCmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD, pt.x, pt.y, m_hMessageWnd, NULL);
    if (iCmd > 0) {
        // Step 7: If a command was selected, invoke it.
        // The command ID is 1-based, but InvokeCommand expects a 0-based index.
        InvokeCommand(iCmd - idCmdFirst, pt);
    }

    // Step 8: Clean up resources for this operation.
    DestroyMenu(hMenu);
    ReleaseAll(); // Final cleanup for this operation.
    return SCM_RESULT::Success;
}

/**
 * @brief Removes menu items by their canonical verb string (e.g., "copy", "delete").
 * @details Iterates through the menu, gets the verb for each shell command, and removes
 *          it if it's in the provided list of verbs to remove.
 */
void ShellContextMenu::RemoveMenuItemsByVerb(HMENU hMenu, std::initializer_list<const WCHAR*> verbsToRemove)
{
    if (!hMenu || !m_pContextMenu || verbsToRemove.size() == 0) {
        return;
    }

    // For efficient lookup, copy the verbs into a hash set.
    // This provides O(1) average time complexity for lookups.
    std::unordered_set<std::wstring> verbSet(verbsToRemove.begin(), verbsToRemove.end());

    // Iterate backwards because we are deleting items by position.
    int itemCount = GetMenuItemCount(hMenu);
    for (int i = itemCount - 1; i >= 0; --i) {
        UINT cmd = GetMenuItemID(hMenu, i);

        // Check if the command ID is within the range we allocated for the shell.
        if (cmd >= idCmdFirst && cmd <= idCmdLast) {
            WCHAR verb[256];
            // Ask the IContextMenu for the verb associated with this command ID.
            HRESULT hr = m_pContextMenu->GetCommandString(
                cmd - idCmdFirst, // Command ID is a 0-based offset.
                GCS_VERBW,        // We want the canonical verb name in Unicode.
                NULL,
                (LPSTR)verb,      // Note: This API is weird, it expects LPSTR but writes WCHARs for GCS_VERBW.
                ARRAYSIZE(verb)
            );

            // Check if the retrieved verb exists in our set of verbs to remove.
            if (SUCCEEDED(hr) && verbSet.count(verb) > 0) {
                DeleteMenu(hMenu, i, MF_BYPOSITION);
            }
        }
    }
}

/**
 * @brief Releases all acquired COM interfaces and frees PIDL memory.
 */
void ShellContextMenu::ReleaseAll() {
    if (m_pContextMenu) { m_pContextMenu->Release(); m_pContextMenu = nullptr; }
    if (m_pContextMenu2) { m_pContextMenu2->Release(); m_pContextMenu2 = nullptr; }
    if (m_pContextMenu3) { m_pContextMenu3->Release(); m_pContextMenu3 = nullptr; }
    if (m_pParentFolder) { m_pParentFolder->Release(); m_pParentFolder = nullptr; }

    // Free each PIDL allocated by the shell.
    for (LPITEMIDLIST pidl : m_pidls) {
        CoTaskMemFree(pidl);
    }
    m_pidls.clear();
    m_parentFolderStr.clear();
}

/**
 * @brief Gets the IShellFolder for the parent directory and creates PIDLs for each file.
 * @details A PIDL (Pointer to an Item ID List) is the shell's way of identifying an object.
 *          To get a context menu, we need the parent folder and the PIDLs of the items within it.
 */
SCM_RESULT ShellContextMenu::GetParentAndPIDLs(const std::vector<std::wstring>& files) {
    if (files.empty()) return SCM_RESULT::InvalidInput;

    // Determine the parent directory from the first file path.
    // This assumes all files share the same parent.
    WCHAR szParentPath[MAX_PATH];
    wcscpy_s(szParentPath, files[0].c_str());
    PathRemoveFileSpecW(szParentPath);
    m_parentFolderStr = szParentPath;

    // Get the IShellFolder for the desktop, which is the root of the shell namespace.
    IShellFolder* pDesktopFolder = nullptr;
    if (FAILED(SHGetDesktopFolder(&pDesktopFolder))) return SCM_RESULT::GetDesktopFolderFailed;

    // Parse the parent path string to get its PIDL.
    LPITEMIDLIST pParentPidl = nullptr;
    HRESULT hr = pDesktopFolder->ParseDisplayName(NULL, NULL, szParentPath, NULL, &pParentPidl, NULL);
    if (FAILED(hr)) {
        pDesktopFolder->Release();
        return SCM_RESULT::GetParentFolderFailed;
    }

    // Bind to the parent folder object itself to get its IShellFolder interface.
    hr = pDesktopFolder->BindToObject(pParentPidl, NULL, IID_IShellFolder, (void**)&m_pParentFolder);
    CoTaskMemFree(pParentPidl); // We no longer need the parent's PIDL.
    pDesktopFolder->Release();  // We no longer need the desktop folder.
    if (FAILED(hr)) return SCM_RESULT::GetParentFolderFailed;

    // For each file, parse its name relative to the parent folder to get its child PIDL.
    for (const auto& file : files) {
        LPITEMIDLIST pFilePidl = nullptr;
        std::wstring fileName = PathFindFileNameW(file.c_str());
        // ParseDisplayName on the parent folder gets the PIDL relative to that folder.
        if (FAILED(m_pParentFolder->ParseDisplayName(NULL, NULL, (LPWSTR)fileName.c_str(), NULL, &pFilePidl, NULL))) {
            return SCM_RESULT::PidlCreationFialed;
        }
        m_pidls.push_back(pFilePidl);
    }
    return SCM_RESULT::Success;
}

/**
 * @brief Retrieves the IContextMenu interfaces for a collection of items.
 */
SCM_RESULT ShellContextMenu::GetContextMenuInterfaces(IShellFolder* pParentFolder, const std::vector<LPCITEMIDLIST>& pidls) {
    if (!pParentFolder || pidls.empty()) return SCM_RESULT::InvalidInput;

    // GetUIObjectOf expects a non-const array, so we must cast away constness.
    // This is safe as the shell documentation confirms it does not modify the PIDLs.
    LPCITEMIDLIST* nonConstPidls = const_cast<LPCITEMIDLIST*>(pidls.data());

    // Ask the parent folder for the UI object that handles the context menu for the given items.
    if (FAILED(pParentFolder->GetUIObjectOf(m_hMessageWnd, static_cast<UINT>(pidls.size()), nonConstPidls, IID_IContextMenu, NULL, (void**)&m_pContextMenu))) {
        return SCM_RESULT::GetContextMenuInterfacesFailed;
    }

    // Also query for the extended interfaces for more advanced message handling.
    // These calls may fail, which is acceptable; we just won't have the extra functionality.
    m_pContextMenu->QueryInterface(IID_IContextMenu2, (void**)&m_pContextMenu2);
    m_pContextMenu->QueryInterface(IID_IContextMenu3, (void**)&m_pContextMenu3);
    return SCM_RESULT::Success;
}

/**
 * @brief Executes a command from the context menu.
 */
void ShellContextMenu::InvokeCommand(UINT iCmd, POINT pt) {
    // CMINVOKECOMMANDINFOEX provides detailed information about the invocation.
    CMINVOKECOMMANDINFOEX cmi = { sizeof(cmi) };
    cmi.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE; // Use Unicode and specify invocation point.

    // Pass modifier key states (Ctrl, Shift) to the command handler.
    if (GetKeyState(VK_CONTROL) < 0) cmi.fMask |= CMIC_MASK_CONTROL_DOWN;
    if (GetKeyState(VK_SHIFT) < 0) cmi.fMask |= CMIC_MASK_SHIFT_DOWN;

    cmi.hwnd = m_hMessageWnd;
    cmi.lpVerb = MAKEINTRESOURCEA(iCmd); // The command is identified by its 0-based offset.
    cmi.lpVerbW = MAKEINTRESOURCEW(iCmd);
    cmi.lpDirectoryW = m_parentFolderStr.c_str(); // The working directory.
    cmi.nShow = SW_SHOWNORMAL;
    cmi.ptInvoke = pt; // The point where the menu was invoked (for "Paste at cursor" etc.).

    // Execute the command.
    m_pContextMenu->InvokeCommand((LPCMINVOKECOMMANDINFO)&cmi);
}

/**
 * @brief Static window procedure that routes messages to the correct class instance.
 */
LRESULT CALLBACK ShellContextMenu::WndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
    // Retrieve the 'this' pointer we stored in the window's user data.
    ShellContextMenu* pThis = (ShellContextMenu*)GetWindowLongPtr(hWnd, GWLP_USERDATA);
    if (pThis) {
        // If we have a valid instance pointer, forward the message to its handler.
        return pThis->MessageHandler(uMsg, wParam, lParam);
    }
    return DefWindowProc(hWnd, uMsg, wParam, lParam);
}

/**
 * @brief Instance-specific message handler that forwards menu messages to the shell interfaces.
 * @details This allows shell extensions to perform custom drawing or other handling.
 */
LRESULT ShellContextMenu::MessageHandler(UINT uMsg, WPARAM wParam, LPARAM lParam) {
    // If we have an IContextMenu2 interface, forward relevant messages to it.
    // This is necessary for owner-drawn menu items.
    if (m_pContextMenu2 && (uMsg == WM_INITMENUPOPUP || uMsg == WM_DRAWITEM || uMsg == WM_MEASUREITEM)) {
        if (SUCCEEDED(m_pContextMenu2->HandleMenuMsg(uMsg, wParam, lParam))) {
            return 0; // The message was handled.
        }
    }

    // If we have an IContextMenu3 interface, forward relevant messages to it.
    if (m_pContextMenu3 && uMsg == WM_MENUCHAR) {
        LRESULT result = 0;
        if (SUCCEEDED(m_pContextMenu3->HandleMenuMsg2(uMsg, wParam, lParam, &result))) {
            return result; // Return the result from the handler.
        }
    }

    // For all other messages, use the default window procedure.
    return DefWindowProc(m_hMessageWnd, uMsg, wParam, lParam);
}