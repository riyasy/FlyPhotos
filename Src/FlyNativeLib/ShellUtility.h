/**
 * @file ShellUtility.h
 * @brief Declares the ShellUtility class for Windows Shell helper functions.
 *
 * This class provides static methods for interacting with the Windows Shell,
 * such as retrieving the list of files from the active Explorer window.
 */

#pragma once

#include <Windows.h>
#include <vector>
#include <string>

using namespace std;

/// @brief Provides static utility methods for interacting with the Windows Shell.
class __declspec(dllexport) ShellUtility
{
public:
	/// @brief Constructs a ShellUtility object.
	ShellUtility();

	/// @brief Destructs the ShellUtility object.
	~ShellUtility();

	/// @brief Displays a simple Windows message box with a given phrase.
	/// @param phrase The text (UTF-16) to display in the message box.
	static void SayThis(const wchar_t* phrase);

	/// @brief Retrieves a list of full paths for all items in the active Windows Explorer window.
	/// @param arr A reference to a vector of wstring that will be populated with the file paths.
	/// @param hwndExplorer The Explorer window to enumerate, or nullptr to use GetForegroundWindow().
	/// @return An HRESULT indicating success (S_OK) or an error code. On failure this is
	///         0x8004020n, where n is the enumeration stage that gave up — see the table at the
	///         bottom of GetFileListFromExplorerWindow.
	/// @note This function appends the found paths to the vector; it does not clear it first.
	/// @note Prefer passing an explicitly captured HWND. Resolving the foreground window inside this
	///       call races the caller's own window activation: once the caller is foreground, no
	///       ShellTabWindowClass child is found and enumeration fails at stage 0.
	static HRESULT GetFileListFromExplorerWindow(vector<wstring>& arr, HWND hwndExplorer = nullptr);
};