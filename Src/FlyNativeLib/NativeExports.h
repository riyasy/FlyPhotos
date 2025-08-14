#pragma once

#include <Windows.h>

// This defines the function signature for a callback that we will pass from C# to C++.
// It will be called for each file found.
typedef void(__stdcall* FileListCallback)(const wchar_t* filePath);

// We define our exported functions inside extern "C" to prevent C++ name mangling.
#ifdef __cplusplus
extern "C" {
#endif

    // The __declspec(dllexport) keyword makes the function visible to other modules.
    __declspec(dllexport) HRESULT GetFileListFromExplorer(FileListCallback callback);
    __declspec(dllexport) bool ShowExplorerContextMenu(const wchar_t* filePath, int x, int y);

    // We can handle the WIC codec list similarly with another callback.
    typedef void(__stdcall* CodecInfoCallback)(const wchar_t* friendlyName, const wchar_t* extensions);
    __declspec(dllexport) HRESULT GetWicDecoders(CodecInfoCallback callback);

#ifdef __cplusplus
}
#endif