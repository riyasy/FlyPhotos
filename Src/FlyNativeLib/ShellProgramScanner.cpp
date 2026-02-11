/**
 * @file ShellProgramScanner.cpp
 * @brief Implements ShellProgramScanner - enumerates Start Menu shortcuts and extracts 32bpp icon bitmaps.
 *
 * @details
 * This translation unit provides the implementation for ShellProgramScanner which scans the
 * well-known Start Menu locations (per-user and all-users) for ".lnk" shortcut files, resolves
 * each shortcut to its target executable and extracts a 32bpp BGRA bitmap for display in UI lists.
 * The implementation uses COM Shell APIs and an IImageList (system image list) to obtain icon
 * bitmaps. Errors are handled on a best-effort basis so enumeration will continue even if
 * individual shortcuts or shell extensions fail.
 */

#include "pch.h"
#include "ShellProgramScanner.h"

#include <shlobj.h>
#include <shlguid.h>
#include <commoncontrols.h>
#include <filesystem>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

namespace fs = std::filesystem;

/**
 * @brief Constructs a ShellProgramScanner instance.
 *
 * The constructor initializes internal COM pointer fields to null. No COM initialization
 * is performed here; callers should invoke Scan() which will perform CoInitialize/CoUninitialize
 * for the scope of the operation.
 */
ShellProgramScanner::ShellProgramScanner()
    : _psl(nullptr), _ppf(nullptr), _imageList32(nullptr)
{
}

/**
 * @brief Destructor.
 *
 * Releases any remaining COM pointers (if Scan failed partway) — the destructor intentionally
 * does not call CoUninitialize because Scan manages COM initialization itself.
 */
ShellProgramScanner::~ShellProgramScanner()
{
}

/**
 * @brief Returns the collected ShortcutData results.
 */
const std::vector<ShortcutData>& ShellProgramScanner::GetResults() const
{
    return _results;
}


/**
 * @brief Performs a synchronous scan of Start Menu folders.
 *
 * This method will:
 *  - Initialize COM for the calling thread (CoInitialize/CoUninitialize).
 *  - Obtain the system image list (IImageList) for large icons.
 *  - Create an IShellLink/IPersistFile pair to resolve .lnk shortcuts.
 *  - Walk known folders FOLDERID_CommonPrograms and FOLDERID_Programs recursively.
 *  - For each discovered .lnk file attempt to resolve and extract a 32bpp icon bitmap.
 *
 * The function swallows non-fatal failures to provide best-effort enumeration.
 */
void ShellProgramScanner::Scan()
{
    _results.clear();
    _results.reserve(128);

    CoInitialize(nullptr);

    if (FAILED(SHGetImageList(SHIL_LARGE, IID_PPV_ARGS(&_imageList32))))
    {
        CoUninitialize();
        return;
    }

    if (SUCCEEDED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&_psl))))
    {
        if (SUCCEEDED(_psl->QueryInterface(
            IID_PPV_ARGS(&_ppf))))
        {
            PWSTR commonPrograms = nullptr;
            PWSTR userPrograms = nullptr;

            if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_CommonPrograms, 0, nullptr, &commonPrograms)))
            {
                ScanDirectory(commonPrograms);
                CoTaskMemFree(commonPrograms);
            }

            if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Programs, 0, nullptr, &userPrograms)))
            {
                ScanDirectory(userPrograms);
                CoTaskMemFree(userPrograms);
            }

            _ppf->Release();
            _ppf = nullptr;
        }

        _psl->Release();
        _psl = nullptr;
    }

    if (_imageList32)
    {
        _imageList32->Release();
        _imageList32 = nullptr;
    }

    CoUninitialize();
}


/**
 * @brief Recursively scans a directory for ".lnk" shortcut files.
 *
 * This helper uses std::filesystem::recursive_directory_iterator with
 * skip_permission_denied to avoid failing on protected folders. Individual
 * iteration errors are caught and ignored to allow scanning to continue.
 */
void ShellProgramScanner::ScanDirectory(const std::wstring& folderPath)
{
    if (!fs::exists(folderPath))
        return;

    try
    {
        for (const auto& entry :
            fs::recursive_directory_iterator(folderPath, fs::directory_options::skip_permission_denied))
        {
            if (entry.is_regular_file() && entry.path().extension() == L".lnk")
            {
                ResolveLink(entry.path().wstring());
            }
        }
    }
    catch (...)
    {
        // best-effort: ignore iteration or permission errors
    }
}


/**
 * @brief Resolves a .lnk file to its target and attempts to extract its executable icon.
 *
 * Loads the shortcut via IPersistFile::Load and calls IShellLink::Resolve with flags
 * that avoid UI and searching. Only .exe targets are considered for icon extraction.
 */
void ShellProgramScanner::ResolveLink(const std::wstring& lnkPath)
{
    if (FAILED(_ppf->Load(
        lnkPath.c_str(),
        STGM_READ | STGM_SHARE_DENY_NONE)))
        return;

    DWORD flags = SLR_NO_UI | SLR_NOUPDATE | SLR_NOSEARCH | SLR_NOTRACK;

    if (FAILED(_psl->Resolve(nullptr, flags)))
        return;

    WCHAR targetPath[MAX_PATH]{};
    if (FAILED(_psl->GetPath(targetPath, MAX_PATH, nullptr, 0)))
        return;

    std::wstring exePath = targetPath;
    if (exePath.length() < 4 || exePath.substr(exePath.length() - 4) != L".exe")
        return;

    std::wstring displayName =
        fs::path(lnkPath).stem().wstring();

    ExtractIcon(exePath, displayName);
}


/**
 * @brief Extracts a 32bpp BGRA bitmap for the specified executable using the system image list.
 *
 * The function queries the system icon index for the executable path, obtains an HICON
 * from the IImageList, converts the icon HBITMAP to a 32bpp pixel buffer using GetDIBits
 * and stores the result in a ShortcutData entry appended to the results vector.
 */
void ShellProgramScanner::ExtractIcon(const std::wstring& exePath, const std::wstring& displayName)
{
    SHFILEINFOW sfi{};
    if (!SHGetFileInfoW(exePath.c_str(), FILE_ATTRIBUTE_NORMAL, &sfi, sizeof(sfi), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES))
    {
        return;
    }

    HICON hIcon = nullptr;
    if (FAILED(_imageList32->GetIcon(sfi.iIcon, ILD_TRANSPARENT, &hIcon)))
        return;

    ICONINFO ii{};
    if (!GetIconInfo(hIcon, &ii))
    {
        DestroyIcon(hIcon);
        return;
    }

    BITMAP bm{};
    GetObject(ii.hbmColor, sizeof(bm), &bm);

    int width = bm.bmWidth;
    int height = bm.bmHeight;

    std::vector<uint8_t> pixels(width * height * 4);

    BITMAPINFO bmi{};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = width;
    bmi.bmiHeader.biHeight = -height; // top-down
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    HDC hdc = GetDC(nullptr);
    GetDIBits(hdc, ii.hbmColor, 0, height, pixels.data(), &bmi, DIB_RGB_COLORS);
    ReleaseDC(nullptr, hdc);

    DeleteObject(ii.hbmColor);
    DeleteObject(ii.hbmMask);
    DestroyIcon(hIcon);

    // Premultiply alpha (BGRA → BGRA premultiplied)
    uint8_t* p = pixels.data();
    size_t count = width * height;
    for (size_t i = 0; i < count; i++)
    {
        uint8_t a = p[3];
        if (a != 255) // small optimization
        {
            p[0] = (p[0] * a + 127) / 255; // B
            p[1] = (p[1] * a + 127) / 255; // G
            p[2] = (p[2] * a + 127) / 255; // R
        }
        p += 4;
    }

    ShortcutData data;
    data.Name = displayName;
    data.Path = exePath;
    data.Width = width;
    data.Height = height;
    data.Pixels = std::move(pixels);

    _results.push_back(std::move(data));
}
