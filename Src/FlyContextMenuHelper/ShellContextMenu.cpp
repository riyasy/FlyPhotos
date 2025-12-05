/**
 * @file ShellContextMenu.cpp
 * @brief Implements the ShellContextMenu class for displaying the native Windows Shell context menu.
 *
 * @details
 * Implementation Notes:
 * This implementation handles the complex interactions between Win32, COM, and Shell Interfaces
 * (IContextMenu, IContextMenu2, IContextMenu3).
 */

#include "ShellContextMenu.h"
#include <shlwapi.h>
#include <string>
#include <iostream>
#include <commctrl.h>

 // Link against necessary Windows libraries
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "comctl32.lib")
// Note: Shell32.lib is also required for ILCreateFromPathW/SHBindToParent but is often linked by default. 
// If linking errors occur, #pragma comment(lib, "shell32.lib") may be needed.

// -----------------------------------------------------------------------------
// CRASH PROTECTION HELPERS (SEH)
// -----------------------------------------------------------------------------
// These functions wrap critical Shell calls in __try/__except blocks.
// They are separated into static functions to avoid C2712 compiler errors 
// (Mixing C++ object unwinding with SEH in the same function).

/**
 * @brief safely retrieves a verb string from a context menu interface.
 *
 * Third-party shell extensions are notoriously unstable. If they crash inside GetCommandString,
 * the entire application would normally terminate. This function wraps the call in a
 * __try/__except block to catch access violations and other hardware exceptions.
 *
 * @note This function must NOT contain C++ objects with destructors (like std::string)
 *       in the same scope as the __try block, due to compiler limitations (C2712).
 *
 * @param pCtx The IContextMenu pointer from which to query the command string.
 * @param idCmd The command offset (zero-based) to query.
 * @param pszName Pointer to a buffer that receives the verb string (ANSI/Unicode depending on usage).
 * @param cchMax The size, in characters, of the buffer pointed to by pszName.
 * @return HRESULT S_OK on success, or a failure code if an exception occurred or the call failed.
 */
static HRESULT SafeGetCommandString(IContextMenu* pCtx, UINT_PTR idCmd, LPSTR pszName, UINT cchMax)
{
    if (!pCtx) return E_INVALIDARG;
    HRESULT hr = E_FAIL;
    __try {
        hr = pCtx->GetCommandString(idCmd, GCS_VERBW, NULL, pszName, cchMax);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        // Swallow the crash to protect the host app
        OutputDebugStringW(L"ShellContextMenu: Extension crashed inside GetCommandString.\n");
        hr = E_FAIL;
    }
    return hr;
}

/**
 * @brief Safely queries the context menu to populate the HMENU.
 *
 * Similar to SafeGetCommandString, this protects the host application from crashes
 * occurring during the initialization of shell extensions (which often happens inside QueryContextMenu).
 *
 * @param pCtx The IContextMenu pointer to call QueryContextMenu on.
 * @param hMenu The HMENU to populate with items.
 * @param indexMenu The zero-based position to insert items.
 * @param idCmdFirst The first command identifier available for the extension.
 * @param idCmdLast The last command identifier available for the extension.
 * @param uFlags Flags that control which menu items are added (CMF_* constants).
 * @return HRESULT S_OK on success, or a failure HRESULT if the extension crashed or returned an error.
 */
static HRESULT SafeQueryContextMenu(IContextMenu* pCtx, HMENU hMenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT uFlags)
{
    if (!pCtx) return E_INVALIDARG;
    HRESULT hr = E_FAIL;
    __try {
        // This is where bad extensions (PDF handlers, SVN tools) typically crash.
        hr = pCtx->QueryContextMenu(hMenu, indexMenu, idCmdFirst, idCmdLast, uFlags);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        OutputDebugStringW(L"ShellContextMenu: Extension crashed inside QueryContextMenu.\n");
        hr = E_FAIL;
    }
    return hr;
}
// -----------------------------------------------------------------------------

/**
 * @brief Constructs a ShellContextMenu object and initializes member variables to a null state.
 *
 * @note No resources are acquired in the constructor; call Init() before using the object.
 */
ShellContextMenu::ShellContextMenu()
    : m_isInitialized(false), m_pContextMenu(nullptr),
    m_pContextMenu2(nullptr), m_pContextMenu3(nullptr), m_pParentFolder(nullptr), m_hOwner(NULL)
{
}

/**
 * @brief Destructor that ensures all acquired resources are released.
 *
 * @note Releases COM interfaces and uninitializes OLE if Init() succeeded.
 */
ShellContextMenu::~ShellContextMenu() {
    ReleaseAll();
    if (m_isInitialized) {
        OleFlushClipboard();
        OleUninitialize();
    }
}

/**
 * @brief Performs necessary one-time initialization for the class instance.
 *
 * Initializes the OLE libraries required for Shell COM interaction.
 *
 * @return SCM_RESULT::Success on success.
 * @return SCM_RESULT::OleInitializeFailed if OleInitialize fails with an unexpected error.
 */
SCM_RESULT ShellContextMenu::Init() {
    if (m_isInitialized) return SCM_RESULT::Success;

    HRESULT hr = OleInitialize(NULL);
    // RPC_E_CHANGED_MODE means the thread is already initialized as MTA (Multi-Threaded Apartment). 
    // This is "okay" generally, but some older shell extensions requiring STA might fail.
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) {
        return SCM_RESULT::OleInitializeFailed;
    }

    m_isInitialized = true;
    return SCM_RESULT::Success;
}

/**
 * @brief Displays the shell context menu for a given set of files at a specific point.
 *
 * This is the main entry point. It orchestrates:
 * 1. Resolving PIDLs for the files.
 * 2. Loading the IContextMenu interfaces.
 * 3. Creating and populating the Win32 Popup Menu.
 * 4. Managing Thread Input attachment (critical for focus/tooltips).
 * 5. Displaying the menu and waiting for user selection.
 * 6. Invoking the selected command.
 *
 * @param owner The window handle that will own the menu and receive messages.
 * @param files Vector of file paths to include in the menu.
 * @param pt Screen coordinates for the menu position.
 * @return SCM_RESULT::Success on success, or an error code describing the failure.
 */
SCM_RESULT ShellContextMenu::ShowContextMenu(HWND owner, const std::vector<std::wstring>& files, POINT pt) {
    ReleaseAll();

    // Record owner for MessageHandler default routing and InvokeCommand parentage
    m_hOwner = owner;

    // 1. Generate PIDLs (Item ID Lists) for the target files
    SCM_RESULT result = GetParentAndPIDLs(files);
    if (result != SCM_RESULT::Success) { ReleaseAll(); return result; }

    // 2. Get the IContextMenu interface from the parent folder for these PIDLs
    std::vector<LPCITEMIDLIST> const_pidls(m_pidls.begin(), m_pidls.end());

    // 3. Get Interfaces
    // Pass 'owner' so IContextMenu knows who is asking (helps with some extensions)
    result = GetContextMenuInterfaces(m_pParentFolder, const_pidls, owner);
    if (result != SCM_RESULT::Success) { ReleaseAll(); return result; }

    // 4. Create the Win32 Menu resource
    HMENU hMenu = CreatePopupMenu();
    if (!hMenu) { ReleaseAll(); return SCM_RESULT::MenuCreationFailed; }

    // Determine flags: CMF_EXTENDEDVERBS adds "Open With", etc., if Shift is held.
    UINT uFlags = CMF_NORMAL | CMF_EXPLORE;
    if (GetKeyState(VK_SHIFT) < 0) uFlags |= CMF_EXTENDEDVERBS;

    // 5. Ask the Shell to populate the menu (using SEH safety wrapper)
    if (FAILED(SafeQueryContextMenu(m_pContextMenu, hMenu, 0, idCmdFirst, idCmdLast, uFlags))) {
        DestroyMenu(hMenu);
        ReleaseAll();
        return SCM_RESULT::QueryContextMenuFailed;
    }

    // 6. Filter out unwanted dangerous verbs
    RemoveMenuItemsByVerb(hMenu, { L"delete", L"cut" });

    // 7. --- THREAD INPUT ATTACHMENT LOGIC ---
    // Context menus rely on the owner window being "active" to handle keyboard input and 
    // ensure that sub-menus/tooltips work correctly. If the helper window is hidden or 
    // created on a background thread, we must explicitly bridge the input queues.

    HWND fgWnd = GetForegroundWindow();
    DWORD fgThreadId = 0;
    if (fgWnd) fgThreadId = GetWindowThreadProcessId(fgWnd, NULL);
    DWORD ownerThreadId = GetWindowThreadProcessId(owner, NULL);
    DWORD thisThreadId = GetCurrentThreadId();

    // Attach to the current foreground window (likely the user's active app)
    BOOL attachedToForeground = FALSE;
    if (fgThreadId != 0 && fgThreadId != thisThreadId)
        if (AttachThreadInput(thisThreadId, fgThreadId, TRUE))
            attachedToForeground = TRUE;

    // Attach to the owner window (our hidden helper window)
    BOOL attachedToOwner = FALSE;
    if (ownerThreadId != 0 && ownerThreadId != thisThreadId && ownerThreadId != fgThreadId)
        if (AttachThreadInput(thisThreadId, ownerThreadId, TRUE))
            attachedToOwner = TRUE;

    // Force the owner window to the foreground to capture menu events
    BringWindowToTop(owner);
    SetForegroundWindow(owner);
    SetActiveWindow(owner);

    // 8. Show Menu
    // Note: TPM_NOANIMATION can make it feel snappier, optional.
    // We use 'owner' (the WinUI window) so the menu is modal and focus works right. (Blocking call)
    UINT iCmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD, pt.x, pt.y, owner, NULL);

    // 8. If a selection was made, invoke the command
    if (iCmd > 0) {
        // Fix for lnt-arithmetic-overflow:
        // Explicitly cast the subtraction result to UINT to tell the compiler 
        // "I know what I'm doing, treat this as an unsigned integer offset".
        InvokeCommand(static_cast<UINT>(iCmd - idCmdFirst), pt, owner);
    }


    // --- CLEANUP ---

    // Detach from threads
    if (attachedToOwner) {
        AttachThreadInput(thisThreadId, ownerThreadId, FALSE);
    }
    if (attachedToForeground) {
        AttachThreadInput(thisThreadId, fgThreadId, FALSE);
    }

    // Post benign message to owner to clear internal menu loop state
    PostMessage(owner, WM_NULL, 0, 0);

    // Clear owner reference
    m_hOwner = NULL;

    DestroyMenu(hMenu);
    ReleaseAll();
    return SCM_RESULT::Success;
}

/**
 * @brief Removes menu items by their canonical verb string.
 *
 * Iterates through the generated menu, queries the verb for each item (using SEH),
 * and deletes items that match the exclusion list (e.g., preventing accidental deletion).
 *
 * @param hMenu The handle to the populated context menu.
 * @param verbsToRemove Initializer list of verbs to remove.
 * @return void
 */
void ShellContextMenu::RemoveMenuItemsByVerb(HMENU hMenu, std::initializer_list<const WCHAR*> verbsToRemove)
{
    if (!hMenu || !m_pContextMenu || verbsToRemove.size() == 0) return;

    std::unordered_set<std::wstring> verbSet(verbsToRemove.begin(), verbsToRemove.end());
    int itemCount = GetMenuItemCount(hMenu);

    // Iterate backwards to avoid index shifting issues when deleting items
    for (int i = itemCount - 1; i >= 0; --i) {
        UINT cmd = GetMenuItemID(hMenu, i);

        if (cmd >= idCmdFirst && cmd <= idCmdLast) {
            WCHAR verb[256] = { 0 };
            // Use Safe Helper
            HRESULT hr = SafeGetCommandString(m_pContextMenu, static_cast<UINT_PTR>(cmd - idCmdFirst), (LPSTR)verb, ARRAYSIZE(verb));
            if (SUCCEEDED(hr)) {
                verb[255] = 0; // Ensure null termination
                if (verbSet.count(verb) > 0) DeleteMenu(hMenu, i, MF_BYPOSITION);
            }
        }
    }
}

/**
 * @brief Releases all acquired COM interfaces and frees PIDL memory.
 *
 * @return void
 */
void ShellContextMenu::ReleaseAll() {
    if (m_pContextMenu) { m_pContextMenu->Release(); m_pContextMenu = nullptr; }
    if (m_pContextMenu2) { m_pContextMenu2->Release(); m_pContextMenu2 = nullptr; }
    if (m_pContextMenu3) { m_pContextMenu3->Release(); m_pContextMenu3 = nullptr; }
    if (m_pParentFolder) { m_pParentFolder->Release(); m_pParentFolder = nullptr; }
    for (LPITEMIDLIST pidl : m_pidls) CoTaskMemFree(pidl);
    m_pidls.clear();
    m_parentFolderStr.clear();

    m_hOwner = NULL;
}


/**
 * @brief Gets the IShellFolder for the parent directory and creates PIDLs for each file.
 * @details Uses ILCreateFromPathW and SHBindToParent to correctly handle special paths
 * (like Drive Roots 'C:\') that manual string manipulation would fail on.
 *
 * @param files A vector of full file paths (UTF-16) to create PIDLs for. The first item is used to determine the parent folder.
 * @return SCM_RESULT::Success on success, or a specific SCM_RESULT error code on failure.
 */
SCM_RESULT ShellContextMenu::GetParentAndPIDLs(const std::vector<std::wstring>& files) {
    if (files.empty()) return SCM_RESULT::InvalidInput;

    // IMPROVED LOGIC: Uses ILCreateFromPath -> SHBindToParent.
    // This correctly handles Drive Roots (C:\) and virtual paths.

    // 1. Get full PIDL of the first file
    LPITEMIDLIST pidlFull = ILCreateFromPathW(files[0].c_str());
    if (!pidlFull) return SCM_RESULT::PidlCreationFialed;

    // 2. SHBindToParent splits the full PIDL into the Parent IShellFolder and the Child PIDL
    LPCITEMIDLIST pidlChild = nullptr;
    HRESULT hr = SHBindToParent(pidlFull, IID_IShellFolder, (void**)&m_pParentFolder, &pidlChild);

    if (FAILED(hr)) {
        ILFree(pidlFull);
        return SCM_RESULT::GetParentFolderFailed;
    }

    // 3. Store parent directory string for InvokeCommand (optional, but good for compatibility)
    // Note: SHBindToParent doesn't give the string path, so we keep your string logic just for m_parentFolderStr
    // or extract it from m_pParentFolder if needed. For now, your string logic is fine for the path variable.
    WCHAR szParentPath[MAX_PATH];
    wcscpy_s(szParentPath, files[0].c_str());
    PathRemoveFileSpecW(szParentPath);
    m_parentFolderStr = szParentPath;

    // 4. Create Child PIDLs
    // For the first item, we already have the child PIDL relative to the parent.
    // We must deep copy it because ILFree(pidlFull) will kill the pointer 'pidlChild' points to.
    m_pidls.push_back(ILClone(pidlChild));

    ILFree(pidlFull); // Release the full PIDL

    // 5. Process remaining files (if multi-selection)
    // Subsequent files are parsed relative to the parent folder found in step 1.
    for (size_t i = 1; i < files.size(); i++) {
        LPITEMIDLIST pidlItem = nullptr;
        const std::wstring& file = files[i];
        LPCWSTR filename = PathFindFileNameW(file.c_str());

        if (SUCCEEDED(m_pParentFolder->ParseDisplayName(NULL, NULL, (LPWSTR)filename, NULL, &pidlItem, NULL))) {
            m_pidls.push_back(pidlItem);
        }
    }

    return SCM_RESULT::Success;
}

/**
 * @brief Retrieves the IContextMenu interfaces (V1, V2, V3) for the collection of items.
 *
 * @param pParentFolder The folder containing the items.
 * @param pidls Vector of PIDLs representing the items.
 * @param owner The HWND to associate with the UI object (important for message routing).
 * @return SCM_RESULT::Success on success, or an error code on failure.
 */
SCM_RESULT ShellContextMenu::GetContextMenuInterfaces(IShellFolder* pParentFolder, const std::vector<LPCITEMIDLIST>& pidls, HWND owner) {
    if (!pParentFolder || pidls.empty()) return SCM_RESULT::InvalidInput;

    LPCITEMIDLIST* nonConstPidls = const_cast<LPCITEMIDLIST*>(pidls.data());

    // Use the owner HWND when requesting UI objects so menu messages are routed to owner.
    HWND hwndForGetUI = owner ? owner : NULL;

    // Request IContextMenu
    if (FAILED(pParentFolder->GetUIObjectOf(hwndForGetUI, static_cast<UINT>(pidls.size()), nonConstPidls, IID_IContextMenu, NULL, (void**)&m_pContextMenu))) {
        return SCM_RESULT::GetContextMenuInterfacesFailed;
    }

    // Try to upgrade to newer interfaces (V2 and V3) if supported by the extensions
    m_pContextMenu->QueryInterface(IID_IContextMenu2, (void**)&m_pContextMenu2);
    m_pContextMenu->QueryInterface(IID_IContextMenu3, (void**)&m_pContextMenu3);
    return SCM_RESULT::Success;
}

/**
 * @brief Executes a command selected from the context menu.
 *
 * Fills the CMINVOKECOMMANDINFOEX structure and calls InvokeCommand.
 * Handles modifiers (Control/Shift) during invocation.
 *
 * @param iCmd Zero-based offset of the command to execute (i.e., iCmd == 0 corresponds to idCmdFirst).
 * @param pt The screen coordinates where the command was invoked.
 * @param owner The HWND to use as the invoking window (used in the CMINVOKECOMMANDINFOEX).
 * @return void
 */
void ShellContextMenu::InvokeCommand(UINT iCmd, POINT pt, HWND owner) {
    CMINVOKECOMMANDINFOEX cmi = { sizeof(cmi) };
    cmi.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;

    if (GetKeyState(VK_CONTROL) < 0) cmi.fMask |= CMIC_MASK_CONTROL_DOWN;
    if (GetKeyState(VK_SHIFT) < 0) cmi.fMask |= CMIC_MASK_SHIFT_DOWN;

    cmi.hwnd = owner;
    cmi.lpVerb = MAKEINTRESOURCEA(iCmd);
    cmi.lpVerbW = MAKEINTRESOURCEW(iCmd);
    cmi.lpDirectoryW = m_parentFolderStr.c_str();
    cmi.nShow = SW_SHOWNORMAL;
    cmi.ptInvoke = pt;

    m_pContextMenu->InvokeCommand((LPCMINVOKECOMMANDINFO)&cmi);
}

/**
 * @brief Routes window messages from the main window procedure to the active IContextMenu interface.
 *
 * This acts as a bridge. The Helper Window receives messages (WM_DRAWITEM, WM_INITMENUPOPUP, etc.)
 * and calls this method. This method forwards them to the shell extensions so they can draw icons
 * and handle sub-menus.
 *
 * @param uMsg The Windows message ID.
 * @param wParam Message parameter (e.g., ID or char).
 * @param lParam Message parameter (e.g., struct pointer).
 * @param outResult [out] Pointer to store the result code if handled.
 * @return true if the message was handled by the Shell Extension, false otherwise.
 */
bool ShellContextMenu::HandleWindowMessage(UINT uMsg, WPARAM wParam, LPARAM lParam, LRESULT* outResult) {
    // Initialize output
    if (outResult) *outResult = 0;

    // If we haven't built the menu interfaces yet, we can't handle messages.
    if (!m_pContextMenu2 && !m_pContextMenu3) {
        return false;
    }

    // Filter: We only care about menu-related messages
    switch (uMsg) {
    case WM_MEASUREITEM:
    case WM_DRAWITEM:
    case WM_INITMENUPOPUP:
    case WM_MENUCHAR:
        break;
    default:
        return false; // Not a message shell extensions care about
    }

    // PRIORITIZE IContextMenu3 (Windows Vista+)
    // It handles owner-draw PLUS keyboard messages (WM_MENUCHAR).
    if (m_pContextMenu3) {
        LRESULT lRes = 0;
        // HandleMenuMsg2 requires a pointer to LRESULT to return the specific value 
        // the extension wants (e.g., for WM_MENUCHAR).
        HRESULT hr = m_pContextMenu3->HandleMenuMsg2(uMsg, wParam, lParam, &lRes);
        if (SUCCEEDED(hr)) {
            if (outResult) *outResult = lRes;
            return true;
        }
    }
    // FALLBACK to IContextMenu2 (Windows 2000/XP)
    // It handles owner-draw only.
    else if (m_pContextMenu2) {
        HRESULT hr = m_pContextMenu2->HandleMenuMsg(uMsg, wParam, lParam);
        if (SUCCEEDED(hr)) {
            // For WM_DRAWITEM/WM_MEASUREITEM, returning TRUE (1) typically means "handled".
            if (outResult) *outResult = 1;
            return true;
        }
    }
    return false;
}