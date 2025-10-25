/**
 * @file NativeExports.cpp
 * @brief Implements the C-style exported functions defined in NativeExports.h.
 *
 * This file serves as the bridge between the C++ implementation classes (ShellUtility,
 * WicUtility, ShellContextMenu) and the external C-style API. It handles marshalling
 * data and ensures that C++ exceptions do not escape the DLL boundary.
 */

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

/**
 * @brief Implementation of the GetFileListFromExplorer exported function.
 * @details This function retrieves the list of files by calling the ShellUtility class
 * and then invokes the provided callback for each file found.
 */
HRESULT GetFileListFromExplorer(FileListCallback callback)
{
    // Validate the callback pointer to prevent crashes.
    if (!callback) return E_POINTER;

    std::vector<std::wstring> fileListNative;
    ShellUtility shellUtil; // Create an instance of the utility class.

    // Call the underlying implementation to get the file list.
    const HRESULT hr = shellUtil.GetFileListFromExplorerWindow(fileListNative);

    if (SUCCEEDED(hr))
    {
        // Iterate through the results and invoke the callback for each item.
        for (const auto& filePath : fileListNative)
        {
            // For each file found in C++, call the C# callback function.
            callback(filePath.c_str());
        }
    }
    return hr;
}

/**
 * @brief Implementation of the ShowExplorerContextMenu exported function.
 * @details This function acts as a safe wrapper around the ShellContextMenu C++ class.
 * It uses a try/catch block to prevent any C++ exceptions from crossing the DLL boundary,
 * which could crash the calling application.
 */
int ShowExplorerContextMenu(HWND ownerHwnd, const wchar_t* filePath, int x, int y)
{
    try
    {
        // All the logic is now safely inside the try block.
        ShellContextMenu scm;

        // Perform two-step initialization required by the class.
        SCM_RESULT result = scm.Init();
        if (result != SCM_RESULT::Success) {
            return static_cast<int>(result);
        }

        // Prepare the file list for the context menu.
        std::vector<std::wstring> fileList;
        // Reserve memory to prevent multiple reallocations, which can reduce the
        // chance of a std::bad_alloc exception.
        fileList.reserve(1);
        fileList.push_back(filePath);

        // Display the context menu at the specified coordinates.
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

/**
 * @brief Implementation of the GetWicDecoders exported function.
 * @details This function calls the WicUtility class to get a list of all installed
 * image decoders and invokes the provided callback for each one.
 */
HRESULT GetWicDecoders(CodecInfoCallback callback)
{
    // Validate the callback pointer.
    if (!callback) return E_POINTER;

    CAtlList<CCodecInfo> listCodecInfoNative;
    WicUtility wicUtil;

    // Call the underlying implementation to get the codec list.
    const HRESULT hr = wicUtil.GetWicCodecList(listCodecInfoNative);

    if (SUCCEEDED(hr))
    {
        // Iterate through the ATL list of codecs.
        POSITION pos = listCodecInfoNative.GetHeadPosition();
        while (nullptr != pos)
        {
            const CCodecInfo& codecInfoNative = listCodecInfoNative.GetNext(pos);
            // Call the C# callback for each codec found, passing its friendly name and extensions.
            callback(codecInfoNative.m_strFriendlyName, codecInfoNative.m_strFileExtensions);
        }
    }
    return hr;
}