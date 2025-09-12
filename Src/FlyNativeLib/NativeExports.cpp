#pragma once
// --- NativeExports.cpp ---
#include "pch.h"
#include "NativeExports.h"
#include "ShellUtility.h"
#include "WicUtility.h"
#include "ExplorerContextMenu.h" // Assuming this contains ExplorerContextMenu class
#include <vector>
#include <string>
#include <atlstr.h>
#include <atlcoll.h>
#include "DllGlobals.h"

// Implementation for getting the file list
HRESULT GetFileListFromExplorer(FileListCallback callback)
{
    if (!callback) return E_POINTER;

    std::vector<std::wstring> fileListNative;
    ShellUtility shellUtil; // Assuming ShellUtility can be stack-allocated
    const HRESULT hr = shellUtil.GetFileListFromExplorerWindow(fileListNative);

    if (SUCCEEDED(hr))
    {
        for (const auto& filePath : fileListNative)
        {
            // For each file found in C++, call the C# callback function.
            callback(filePath.c_str());
        }
    }
    return hr;
}

// Implementation for showing the context menu
bool ShowExplorerContextMenu(HWND ownerHwnd, const wchar_t* filePath, int x, int y)
{
    return ExplorerContextMenu::ShowContextMenu(g_hInst, ownerHwnd, filePath, x, y);
}

// Implementation for getting WIC codecs
HRESULT GetWicDecoders(CodecInfoCallback callback)
{
    if (!callback) return E_POINTER;

    CAtlList<CCodecInfo> listCodecInfoNative;
    WicUtility wicUtil;
    const HRESULT hr = wicUtil.GetWicCodecList(listCodecInfoNative);

    if (SUCCEEDED(hr))
    {
        POSITION pos = listCodecInfoNative.GetHeadPosition();
        while (nullptr != pos)
        {
            const CCodecInfo& codecInfoNative = listCodecInfoNative.GetNext(pos);
            // Call the C# callback for each codec found.
            callback(codecInfoNative.m_strFriendlyName, codecInfoNative.m_strFileExtensions);
        }
    }
    return hr;
}