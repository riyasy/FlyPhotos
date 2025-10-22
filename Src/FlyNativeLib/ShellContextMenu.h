#pragma once

#include <windows.h>
#include <shlobj.h>
#include <vector>
#include <string>
#include <initializer_list>
#include <unordered_set>

// Defines the possible result codes for the ShellContextMenu operations.
// These are safe to return across a DLL boundary. A result of 0 means success.
enum class SCM_RESULT : int {
	Success = 0,

	// Initialization Errors (from Init method)
	OleInitializeFailed = 100,
	WindowRegistrationFailed = 101,
	WindowCreationFailed = 102,

	// ShowContextMenu Errors
	InvalidInput = 200,                  // Input file list was empty or invalid.
	GetDesktopFolderFailed = 201,        // Failed to get the IShellFolder for the desktop.
	GetParentFolderFailed = 202,         // Failed to get the IShellFolder for the parent directory.
	PidlCreationFialed = 203,            // Failed to create a PIDL for one of the files.
	GetContextMenuInterfacesFailed = 204,// Failed to get IContextMenu interfaces.
	MenuCreationFailed = 205,            // Win32 CreatePopupMenu failed.
	QueryContextMenuFailed = 206,        // IContextMenu::QueryContextMenu failed to populate the menu.

	// A generic error for unexpected C++ exceptions caught at the DLL boundary.
	UnhandledCppException = 300
};


class ShellContextMenu {
public:
	ShellContextMenu();
	~ShellContextMenu();

	// Two-step initialization is required to handle errors without exceptions.
	// Call this after creating an instance and before calling ShowContextMenu.
	SCM_RESULT Init();

	// Displays the context menu for the given files at the specified location.
	SCM_RESULT ShowContextMenu(HWND owner, const std::vector<std::wstring>& files, POINT pt);

private:
	// Internal helper methods, also refactored to return error codes.
	void ReleaseAll();
	SCM_RESULT CreateMessageWindow();
	SCM_RESULT GetParentAndPIDLs(const std::vector<std::wstring>& files);
	SCM_RESULT GetContextMenuInterfaces(IShellFolder* pParentFolder, const std::vector<LPCITEMIDLIST>& pidls);
	void InvokeCommand(UINT iCmd, POINT pt);
	void RemoveMenuItemsByVerb(HMENU hMenu, std::initializer_list<const WCHAR*> verbsToRemove);

	// Window procedure for handling menu messages.
	static LRESULT CALLBACK WndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam);
	LRESULT MessageHandler(UINT uMsg, WPARAM wParam, LPARAM lParam);

	// Member variables
	bool                m_isInitialized;
	HWND                m_hMessageWnd;
	IContextMenu* m_pContextMenu;
	IContextMenu2* m_pContextMenu2;
	IContextMenu3* m_pContextMenu3;
	IShellFolder* m_pParentFolder;
	std::vector<LPITEMIDLIST> m_pidls;
	std::wstring        m_parentFolderStr;

	const UINT idCmdFirst = 1;
	const UINT idCmdLast = 0x7FFF;
};