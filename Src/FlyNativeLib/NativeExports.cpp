#pragma once
// --- NativeExports.cpp ---
#include "pch.h"
#include "NativeExports.h"
#include "ShellUtility.h"
#include "WicUtility.h"
#include <vector>
#include <string>
#include <atlstr.h>
#include <atlcoll.h>
#include "DllGlobals.h"
#include "ShellContextMenu.h"

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
int ShowExplorerContextMenu(HWND ownerHwnd, const wchar_t* filePath, int x, int y)
{
    try
    {
        // All the logic is now safely inside the try block.
        ShellContextMenu scm;

        SCM_RESULT result = scm.Init();
        if (result != SCM_RESULT::Success) {
            return static_cast<int>(result);
        }

        std::vector<std::wstring> fileList;
        // Reserve memory to prevent multiple reallocations, which can reduce the
        // chance of a std::bad_alloc exception.
        fileList.reserve(1);
        fileList.push_back(filePath);

        POINT pt = { x, y };
        result = scm.ShowContextMenu(ownerHwnd, fileList, pt);
        return static_cast<int>(result);
    }
    catch (const std::exception& e)
    {
        // This catches standard C++ exceptions (e.g., std::bad_alloc).
        // For debugging, you can log the exception message.
        // In a release build, you would likely remove this logging.
        std::string errorMessage = "Caught std::exception in ShowShellContextMenu: ";
        errorMessage += e.what();
        OutputDebugStringA(errorMessage.c_str());
        return static_cast<int>(SCM_RESULT::UnhandledCppException);
    }
    catch (...)
    {
        // This is a catch-all for any other type of exception that could be thrown.
        // It provides the ultimate guarantee that no exception will escape the DLL.
        OutputDebugStringA("Caught unknown exception in ShowShellContextMenu.");
        return static_cast<int>(SCM_RESULT::UnhandledCppException);
    }
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