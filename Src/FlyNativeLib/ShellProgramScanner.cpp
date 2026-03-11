/**
 * @file ShellProgramScanner.cpp
 * @brief Implements ShellProgramScanner - enumerates FOLDERID_AppsFolder and extracts icons.
 *
 * @details
 * This translation unit provides the implementation for ShellProgramScanner, which scans
 * the FOLDERID_AppsFolder virtual folder – a single enumeration point that contains both
 * Win32 .lnk shortcuts and UWP/packaged-app tiles – and extracts a 32bpp BGRA bitmap for
 * each discovered entry.
 *
 * Distinguishing Win32 from UWP:
 *   PKEY_Link_TargetParsingPath → VT_LPWSTR  →  Win32 executable; icon via SHGetFileInfoW + IImageList.
 *   Property absent/VT_EMPTY   + PKEY_AppUserModel_ID present  →  UWP tile; icon via IShellItemImageFactory.
 *
 * Errors are handled on a best-effort basis: if any individual shell item fails, enumeration
 * continues for remaining items.
 */

#include "pch.h"
#include "ShellProgramScanner.h"

#include <shlobj.h>
#include <shlguid.h>
#include <commoncontrols.h>
#include <filesystem>
#include <shobjidl.h>
#include <propkey.h>
#include <propvarutil.h>
#include <algorithm>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "propsys.lib")

namespace fs = std::filesystem;

// ---------------------------------------------------------------------------
// Icon size requested from IShellItemImageFactory for UWP apps (pixels).
// 32x32 matches SHIL_LARGE; use 48 if you want SHIL_EXTRALARGE.
// ---------------------------------------------------------------------------
static constexpr int kIconSize = 32;

/**
 * @brief Constructs a ShellProgramScanner instance.
 */
ShellProgramScanner::ShellProgramScanner()
    : _imageList32(nullptr)
{
}

/**
 * @brief Destructor.
 */
ShellProgramScanner::~ShellProgramScanner()
{
}

// ---------------------------------------------------------------------------
// Helper: convert an HBITMAP to a premultiplied-alpha BGRA byte vector.
// Returns false and leaves 'pixels' unmodified on failure.
// ---------------------------------------------------------------------------
bool ShellProgramScanner::HBitmapToPixels(HBITMAP hBitmap, int width, int height,
                             std::vector<uint8_t>& pixels)
{
    BITMAPINFO bmi{};
    bmi.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth       = width;
    bmi.bmiHeader.biHeight      = -height; // top-down
    bmi.bmiHeader.biPlanes      = 1;
    bmi.bmiHeader.biBitCount    = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    pixels.resize(static_cast<size_t>(width) * height * 4);

    HDC hdc = GetDC(nullptr);
    int scanlines = GetDIBits(hdc, hBitmap, 0, height, pixels.data(), &bmi, DIB_RGB_COLORS);
    ReleaseDC(nullptr, hdc);

    if (scanlines == 0)
    {
        pixels.clear();
        return false;
    }

    // Premultiply alpha (BGRA → BGRA premultiplied)
    uint8_t* p     = pixels.data();
    size_t   count = static_cast<size_t>(width) * height;
    for (size_t i = 0; i < count; ++i, p += 4)
    {
        uint8_t a = p[3];
        if (a != 255)
        {
            p[0] = static_cast<uint8_t>((p[0] * a + 127) / 255); // B
            p[1] = static_cast<uint8_t>((p[1] * a + 127) / 255); // G
            p[2] = static_cast<uint8_t>((p[2] * a + 127) / 255); // R
        }
    }
    return true;
}

// ---------------------------------------------------------------------------
// Scan
// ---------------------------------------------------------------------------

/**
 * @brief Enumerates FOLDERID_AppsFolder and populates the _results collection with discovered applications.
 *
 * This method performs a single pass over the Windows Apps virtual folder:
 * 1. It initializes COM and retrieves the system image list (used later for caching Win32 icons).
 * 2. It binds to FOLDERID_AppsFolder and iterates through every IShellItem.
 * 3. For each item, it reads its property store to retrieve its display name.
 *    - **Win32 Apps**: Identified by having a valid `PKEY_Link_TargetParsingPath` that points to a `.exe`.
 *                      If found, it calls `ExtractWin32Icon` which uses `SHGetFileInfoW`.
 *    - **UWP/Store Apps**: Identified by lacking a `.exe` target but having an Application User Model ID 
 *                          (`PKEY_AppUserModel_ID`). If found, it calls `ExtractUwpIcon` which uses 
 *                          `IShellItemImageFactory`.
 *
 * The results are stored internally and can be retrieved via `GetResults()`.
 */
std::vector<ShortcutData> ShellProgramScanner::Scan()
{
    std::vector<ShortcutData> results;
    results.reserve(256);

    HRESULT hrInit = CoInitialize(nullptr);
    bool bCoInit = SUCCEEDED(hrInit);

    // Obtain the system image list for Win32 icon extraction.
    if (FAILED(SHGetImageList(SHIL_LARGE, IID_PPV_ARGS(&_imageList32))))
    {
        if (bCoInit) CoUninitialize();
        return results;
    }

    IShellItem* pAppsFolder = nullptr;
    if (SUCCEEDED(SHGetKnownFolderItem(FOLDERID_AppsFolder, KF_FLAG_DEFAULT, nullptr,
                                        IID_PPV_ARGS(&pAppsFolder))))
    {
        IEnumShellItems* pEnum = nullptr;
        if (SUCCEEDED(pAppsFolder->BindToHandler(nullptr, BHID_EnumItems, IID_PPV_ARGS(&pEnum))))
        {
            IShellItem* pItem = nullptr;
            while (pEnum->Next(1, &pItem, nullptr) == S_OK)
            {
                IPropertyStore* pProps = nullptr;
                if (SUCCEEDED(pItem->BindToHandler(nullptr, BHID_PropertyStore, IID_PPV_ARGS(&pProps))))
                {
                    // Read the display name once – used by both branches.
                    PWSTR pName = nullptr;
                    std::wstring displayName;
                    if (SUCCEEDED(pItem->GetDisplayName(SIGDN_NORMALDISPLAY, &pName)) && pName)
                    {
                        displayName = pName;
                        CoTaskMemFree(pName);
                    }

                    if (!displayName.empty())
                    {
                        // -------------------------------------------------------
                        // Branch A: Win32 – PKEY_Link_TargetParsingPath is a path
                        //           to an .exe file (not an installer stub).
                        // -------------------------------------------------------
                        PROPVARIANT varTarget;
                        PropVariantInit(&varTarget);

                        if (SUCCEEDED(pProps->GetValue(PKEY_Link_TargetParsingPath, &varTarget))
                            && varTarget.vt == VT_LPWSTR
                            && varTarget.pwszVal != nullptr)
                        {
                            std::wstring exePath  = varTarget.pwszVal;
                            std::wstring lowerPath = exePath;
                            std::transform(lowerPath.begin(), lowerPath.end(),
                                           lowerPath.begin(), ::towlower);

                            if (lowerPath.size() >= 4
                                && lowerPath.substr(lowerPath.size() - 4) == L".exe"
                                && lowerPath.find(L"\\installer\\") == std::wstring::npos)
                            {
                                ExtractWin32Icon(results, exePath, displayName);
                            }
                        }
                        else
                        {
                            // -------------------------------------------------------
                            // Branch B: UWP/packaged app – no TargetParsingPath.
                            //           Read PKEY_AppUserModel_ID for the AUMID.
                            // -------------------------------------------------------
                            PROPVARIANT varAumid;
                            PropVariantInit(&varAumid);

                            if (SUCCEEDED(pProps->GetValue(PKEY_AppUserModel_ID, &varAumid))
                                && varAumid.vt == VT_LPWSTR
                                && varAumid.pwszVal != nullptr)
                            {
                                std::wstring aumid = varAumid.pwszVal;
                                if (!aumid.empty())
                                {
                                    ExtractUwpIcon(results, pItem, displayName, aumid);
                                }
                            }
                            PropVariantClear(&varAumid);
                        }

                        PropVariantClear(&varTarget);
                    }

                    pProps->Release();
                }
                pItem->Release();
            }
            pEnum->Release();
        }
        pAppsFolder->Release();
    }

    if (_imageList32)
    {
        _imageList32->Release();
        _imageList32 = nullptr;
    }
    if (bCoInit)
    {
        CoUninitialize();
    }
    
    return results;
}

// ---------------------------------------------------------------------------
// ExtractWin32Icon
// ---------------------------------------------------------------------------

/**
 * @brief Extracts a 32bpp BGRA bitmap for a Win32 .exe via the system image list.
 *
 * SHGetFileInfoW looks up the cached icon index; IImageList::GetIcon retrieves the
 * HICON at SHIL_LARGE (32x32) size; GetDIBits converts it to raw pixels.
 */
void ShellProgramScanner::ExtractWin32Icon(std::vector<ShortcutData>& results,
                                           const std::wstring& exePath,
                                           const std::wstring& displayName)
{
    SHFILEINFOW sfi{};
    if (!SHGetFileInfoW(exePath.c_str(), FILE_ATTRIBUTE_NORMAL, &sfi, sizeof(sfi),
                        SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES))
        return;

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
    int width  = bm.bmWidth;
    int height = bm.bmHeight;

    std::vector<uint8_t> pixels;
    bool ok = HBitmapToPixels(ii.hbmColor, width, height, pixels);

    DeleteObject(ii.hbmColor);
    DeleteObject(ii.hbmMask);
    DestroyIcon(hIcon);

    if (!ok) return;

    ShortcutData data;
    data.Name   = displayName;
    data.Path   = exePath;
    data.IsUwp  = false;
    data.Width  = width;
    data.Height = height;
    data.Pixels = std::move(pixels);
    results.push_back(std::move(data));
}

// ---------------------------------------------------------------------------
// ExtractUwpIcon
// ---------------------------------------------------------------------------

/**
 * @brief Extracts a 32bpp BGRA bitmap for a UWP/packaged app via IShellItemImageFactory.
 *
 * Windows resolves the package logo path internally – no manifest parsing needed.
 * The returned HBITMAP is a 32bpp DIB section; GetDIBits converts it to raw pixels.
 */
void ShellProgramScanner::ExtractUwpIcon(std::vector<ShortcutData>& results,
                                         IShellItem* pItem,
                                         const std::wstring& displayName,
                                         const std::wstring& aumid)
{
    IShellItemImageFactory* pImgFactory = nullptr;
    if (FAILED(pItem->QueryInterface(IID_PPV_ARGS(&pImgFactory))))
        return;

    SIZE iconSize{ kIconSize, kIconSize };
    HBITMAP hBitmap = nullptr;
    HRESULT hr = pImgFactory->GetImage(iconSize, SIIGBF_RESIZETOFIT, &hBitmap);
    pImgFactory->Release();

    if (FAILED(hr) || hBitmap == nullptr)
        return;

    std::vector<uint8_t> pixels;
    bool ok = HBitmapToPixels(hBitmap, kIconSize, kIconSize, pixels);
    DeleteObject(hBitmap);

    if (!ok) return;

    ShortcutData data;
    data.Name   = displayName;
    data.Aumid  = aumid;
    data.IsUwp  = true;
    data.Width  = kIconSize;
    data.Height = kIconSize;
    data.Pixels = std::move(pixels);
    results.push_back(std::move(data));
}

// ---------------------------------------------------------------------------
// GetUwpIconByAumid
// ---------------------------------------------------------------------------

bool ShellProgramScanner::GetUwpIconByAumid(const std::wstring& aumid, std::vector<uint8_t>& outPixels, int& outWidth, int& outHeight)
{
    if (aumid.empty()) return false;

    // The parsing name for an item in the Apps folder is "shell:AppsFolder\<AUMID>"
    std::wstring parsingName = L"shell:AppsFolder\\";
    parsingName += aumid;

    IShellItem* pItem = nullptr;
    HRESULT hr = SHCreateItemFromParsingName(parsingName.c_str(), nullptr, IID_PPV_ARGS(&pItem));
    if (FAILED(hr)) return false;

    IShellItemImageFactory* pImgFactory = nullptr;
    hr = pItem->QueryInterface(IID_PPV_ARGS(&pImgFactory));
    pItem->Release();
    if (FAILED(hr)) return false;

    // 32x32 is the requested size used in Scan()
    SIZE iconSize{ 32, 32 };
    HBITMAP hBitmap = nullptr;
    // SIIGBF_ICONONLY ensures we get the unplated icon, identical to the scan.
    hr = pImgFactory->GetImage(iconSize, SIIGBF_ICONONLY, &hBitmap);
    pImgFactory->Release();

    if (FAILED(hr) || hBitmap == nullptr) return false;

    BITMAP bm{};
    GetObject(hBitmap, sizeof(bm), &bm);
    outWidth = bm.bmWidth;
    outHeight = bm.bmHeight;

    bool ok = HBitmapToPixels(hBitmap, outWidth, outHeight, outPixels);

    DeleteObject(hBitmap);
    return ok;
}
