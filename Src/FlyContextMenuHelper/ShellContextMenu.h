/**
 * @file ShellContextMenu.h
 * @brief Declares the ShellContextMenu class for displaying the native Windows Shell context menu.
 *
 * This class encapsulates the complex COM and Win32 interactions required to query
 * and display the right-click menu for one or more file system objects.
 *
 * @details
 * Key Features:
 * - In-Process Handling: Runs within the application process.
 * - Hooks the owner window to handle menu messages (WM_DRAWITEM, etc.)
 *   ensuring correct focus, Z-order, and DPI inheritance.
 * - Crash Protection: Uses SEH (Structured Exception Handling) to guard against buggy shell extensions.
 */

#pragma once

#include <windows.h>
#include <shlobj.h>
#include <vector>
#include <string>
#include <initializer_list>
#include <unordered_set>

 /// @brief Defines the possible result codes for the ShellContextMenu operations.
 /// @note These are safe to return across a DLL boundary. A result of 0 means success.
enum class SCM_RESULT : int {
    Success = 0,

    // Initialization Errors (from Init method)
    OleInitializeFailed = 100,
    WindowRegistrationFailed = 101,
    WindowCreationFailed = 102,
    ThreadNotSTA = 103,

    // ShowContextMenu Errors
    InvalidInput = 200,                  ///< Input file list was empty or invalid.
    GetDesktopFolderFailed = 201,        ///< Failed to get the IShellFolder for the desktop.
    GetParentFolderFailed = 202,         ///< Failed to get the IShellFolder for the parent directory.
    PidlCreationFialed = 203,            ///< Failed to create a PIDL for one of the files.
    GetContextMenuInterfacesFailed = 204,///< Failed to get IContextMenu interfaces.
    MenuCreationFailed = 205,            ///< Win32 CreatePopupMenu failed.
    QueryContextMenuFailed = 206,        ///< IContextMenu::QueryContextMenu failed to populate the menu.

    // A generic error for unexpected C++ exceptions caught at the DLL boundary.
    UnhandledCppException = 300
};


/// @brief Manages the creation and display of the Windows Shell context menu.
class ShellContextMenu {
public:
    /// @brief Constructs a ShellContextMenu object.
    ShellContextMenu();
    /// @brief Destructs the ShellContextMenu object, releasing all resources.
    ~ShellContextMenu();

    /// @brief Initializes the object by setting up OLE and creating a hidden message window.
    /// @return SCM_RESULT::Success, or an error code on failure.
    /// @note Must be called successfully before ShowContextMenu.
    SCM_RESULT Init();

    /// @brief Displays the context menu for the given files at the specified location.
    /// @details This method orchestrates the entire lifecycle: obtaining PIDLs, querying interfaces,
    /// showing the menu, and invoking the command.
    /// @param owner The handle of the window that will own the context menu.
    /// @param files A vector of full file paths (UTF-16) to generate the menu for.
    /// @param pt The screen coordinates (POINT) where the menu should appear.
    /// @return SCM_RESULT::Success, or an error code on failure.
    SCM_RESULT ShowContextMenu(HWND owner, const std::vector<std::wstring>& files, POINT pt);

    /// @brief Routes window messages to the active IContextMenu interface (V2 or V3).
    /// @param outResult The result to return to Windows if the message was handled.
    /// @return true if the message was handled by the shell extension; false otherwise.
    bool HandleWindowMessage(UINT uMsg, WPARAM wParam, LPARAM lParam, LRESULT* outResult);

private:
    /// @brief Releases all COM interfaces and frees PIDL memory.
    void ReleaseAll();

    /// @brief Determines the parent folder from a list of files and creates PIDLs for each file.
    /// @details Uses SHBindToParent to safely handle drive roots (C:\) and virtual folders.
    /// @param files A vector of full file paths (UTF-16).
    /// @return SCM_RESULT::Success, or an error code if parsing fails.
    SCM_RESULT GetParentAndPIDLs(const std::vector<std::wstring>& files);

    /// @brief Obtains the IContextMenu, IContextMenu2, and IContextMenu3 interfaces from the parent shell folder.
    /// @param pParentFolder The IShellFolder interface of the parent directory.
    /// @param pidls A vector of PIDLs for the files/items.
    /// @param owner The HWND of the owner window which will receive menu messages (must be in same process/thread).
    /// @return SCM_RESULT::Success, or GetContextMenuInterfacesFailed on error.
    SCM_RESULT GetContextMenuInterfaces(IShellFolder* pParentFolder, const std::vector<LPCITEMIDLIST>& pidls, HWND owner);

    /// @brief Executes a selected context menu command.
    /// @param iCmd The zero-based offset of the command identifier to invoke.
    /// @param pt The screen coordinates where the command was invoked.
    /// @param owner The HWND that was used to display the menu (used as hwnd in CMINVOKECOMMANDINFOEX).
    void InvokeCommand(UINT iCmd, POINT pt, HWND owner);

    /// @brief Removes specific items from the context menu based on their canonical verb.
    /// @param hMenu The handle to the context menu.
    /// @param verbsToRemove An initializer list of verbs (e.g., L"delete", L"cut") to remove.
    void RemoveMenuItemsByVerb(HMENU hMenu, std::initializer_list<const WCHAR*> verbsToRemove);

    // Member variables
    bool m_isInitialized;                       ///< True if Init() has succeeded.
    IContextMenu* m_pContextMenu;               ///< The primary context menu interface.
    IContextMenu2* m_pContextMenu2;             ///< Interface for handling custom-drawn menu items.
    IContextMenu3* m_pContextMenu3;             ///< Interface for handling additional menu messages.
    IShellFolder* m_pParentFolder;              ///< The shell folder of the parent directory.
    std::vector<LPITEMIDLIST> m_pidls;          ///< A list of item ID lists (PIDLs) for the selected files.
    std::wstring m_parentFolderStr;             ///< The parent directory path as a string.

    HWND m_hOwner;                              ///< The owner HWND used when showing the menu (for InvokeCommand).

    const UINT idCmdFirst = 1;                  ///< The minimum command identifier for shell menu items.
    const UINT idCmdLast = 0x7FFF;              ///< The maximum command identifier for shell menu items.
};