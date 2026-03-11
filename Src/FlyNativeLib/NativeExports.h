/**
 * @file NativeExports.h
 * @brief Defines the C-style P/Invoke API for the FlyNativeLib library.
 *
 * This header declares the functions exported from the DLL for use by other
 * languages like C#. It uses extern "C" to prevent C++ name mangling.
 */

#pragma once

#include <Windows.h>
#include <cstdint>  

// We define our exported functions inside extern "C" to prevent C++ name mangling.
#ifdef __cplusplus
extern "C" {
#endif

    // This defines the function signature for a callback that we will pass from C# to C++.
    // It will be called for each file found.
    typedef void(__stdcall* FileListCallback)(const wchar_t* filePath);

    /// @brief Gets the full paths of all items in the active Windows Explorer window.
    /// @param callback A pointer to a callback function that will be invoked for each file path found.
    /// @return An HRESULT indicating success (S_OK) or an error code.
    /// @note The callback function will be called multiple times, once for each item.
    __declspec(dllexport) HRESULT GetFileListFromExplorer(FileListCallback callback);

    /// @brief Displays the native Windows Shell context menu for a specific file.
    /// @param ownerHwnd Handle to the owner window for the context menu.
    /// @param filePath The full path to the file or folder (UTF-16).
    /// @param x The horizontal screen coordinate for the menu's position.
    /// @param y The vertical screen coordinate for the menu's position.
    /// @return An integer result code (0 for success) corresponding to the SCM_RESULT enum.
    __declspec(dllexport) int ShowExplorerContextMenu(HWND ownerHwnd, const wchar_t* filePath, int x, int y);

    // We can handle the WIC codec list similarly with another callback.
    typedef void(__stdcall* CodecInfoCallback)(const wchar_t* friendlyName, const wchar_t* extensions);

    /// @brief Enumerates all installed WIC (Windows Imaging Component) image decoders.
    /// @param callback A pointer to a callback function to receive the codec information.
    /// @return An HRESULT indicating success (S_OK) or an error code.
    /// @note The callback function will be called multiple times, once for each decoder.
    __declspec(dllexport) HRESULT GetWicDecoders(CodecInfoCallback callback);

    /// @brief Callback for enumerating installed apps (Win32 and UWP) from FOLDERID_AppsFolder.
    /// @param name      Display name of the application.
    /// @param path      Full .exe path for Win32 apps; empty string for UWP apps.
    /// @param aumid     Application User Model ID for UWP apps; empty string for Win32 apps.
    /// @param isUwp     Non-zero if the entry is a UWP/packaged app, zero if Win32.
    /// @param pixels    Pointer to the 32bpp BGRA (premultiplied-alpha) icon pixel data.
    /// @param pixelSize Byte length of the pixel buffer (width * height * 4).
    /// @param width     Icon width in pixels.
    /// @param height    Icon height in pixels.
    typedef void(__stdcall* ShortcutCallback)(
        const wchar_t* name,
        const wchar_t* path,
        const wchar_t* aumid,
        int            isUwp,
        const uint8_t* pixels,
        int            pixelSize,
        int            width,
        int            height);

    /// @brief Enumerates all installed apps (Win32 and UWP) from the Apps virtual folder.
    ///        For each app the callback is invoked once with icon data and type information.
    /// @param callback A pointer to a callback function to receive each entry.
    /// @return S_OK on success, or an HRESULT error code.
    __declspec(dllexport) HRESULT EnumerateStartMenuShortcuts(ShortcutCallback callback);

    /// @brief Callback for receiving a single extracted icon.
    /// @param pixels    Pointer to the 32bpp BGRA (premultiplied-alpha) icon pixel data.
    /// @param pixelSize Byte length of the pixel buffer.
    /// @param width     Icon width in pixels.
    /// @param height    Icon height in pixels.
    typedef void(__stdcall* SingleIconCallback)(
        const uint8_t* pixels,
        int            pixelSize,
        int            width,
        int            height);

    /// @brief Extracts the icon for a single UWP app using its AUMID.
    /// @param aumid    The Application User Model ID of the UWP app.
    /// @param callback A pointer to a callback function to receive the icon data.
    /// @return S_OK on success, or an error code.
    __declspec(dllexport) HRESULT GetUwpAppIcon(const wchar_t* aumid, SingleIconCallback callback);

#ifdef __cplusplus
}
#endif