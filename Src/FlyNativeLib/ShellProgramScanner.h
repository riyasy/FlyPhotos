/**
 * @file ShellProgramScanner.h
 * @brief Declares ShellProgramScanner - enumerates the Apps virtual folder and extracts icons.
 *
 * This helper uses FOLDERID_AppsFolder to enumerate both Win32 shortcuts and UWP/packaged-app
 * tiles in a single pass. For each item it extracts a 32bpp BGRA icon bitmap and records
 * enough metadata for the managed layer to construct the right InstalledApp subtype
 * (Win32App vs StoreApp). It is implemented with Shell COM APIs and is intended to be
 * consumed by the native helper library exported to managed code.
 */

#pragma once

#include <windows.h>
#include <shobjidl.h>
#include <vector>
#include <string>

using namespace std;

/**
 * @brief Holds information about a program discovered via the FOLDERID_AppsFolder enumeration.
 *
 * Name         - Display name of the application.
 * Path         - Full path to the executable (.exe). Empty for UWP/packaged apps.
 * Aumid        - Application User Model ID. Non-empty only when IsUwp is true.
 * IsUwp        - true if the entry is a UWP/packaged app; false for a Win32 executable.
 * Width/Height - Dimensions of the bitmap stored in Pixels.
 * Pixels       - Raw BGRA (32bpp, premultiplied alpha) pixel data for the icon.
 */
struct ShortcutData
{
    std::wstring Name;
    std::wstring Path;   // non-empty for Win32
    std::wstring Aumid;  // non-empty for UWP
    bool         IsUwp = false;
    int Width  = 0;
    int Height = 0;
    std::vector<uint8_t> Pixels; // BGRA 32bpp, premultiplied alpha
};

/**
 * @brief Scans the FOLDERID_AppsFolder virtual folder for installed applications and their icons.
 *
 * ShellProgramScanner enumerates the Apps virtual folder which contains both Win32
 * shortcuts and UWP/packaged-app tiles. For each item it reads the relevant shell
 * properties to determine the app type, extracts a 32bpp BGRA icon bitmap using the
 * system image list (Win32) or IShellItemImageFactory (UWP), and stores a ShortcutData
 * entry that the managed layer can convert into the appropriate InstalledApp subtype.
 *
 * Typical usage:
 *  - create an instance of ShellProgramScanner
 *  - call Scan()
 *  - call GetResults() to retrieve discovered ShortcutData entries
 */
class ShellProgramScanner
{
public:
    /**
     * @brief Constructs a ShellProgramScanner instance.
     */
    ShellProgramScanner();

    /**
     * @brief Destructor - releases any allocated resources.
     */
    ~ShellProgramScanner();

    /**
     * @brief Performs the enumeration of installed applications from the Windows Apps folder.
     *
     * This method initializes COM, retrieves the system image list (SHIL_LARGE) for Win32 apps,
     * and iterates through every IShellItem in FOLDERID_AppsFolder.
     *  - It reads PKEY_Link_TargetParsingPath to identify Win32 executables.
     *  - It reads PKEY_AppUserModel_ID to identify packaged UWP apps.
     * 
     * Successively calls ExtractWin32Icon or ExtractUwpIcon and populates the results 
     * vector with 32x32 BGRA icons.
     * 
     * @return A vector of discovered applications and their icons.
     */
    std::vector<ShortcutData> Scan();

    /**
     * @brief Extracts a 32x32 BGRA icon for a single UWP app using its AUMID.
     * 
     * Uses SHCreateItemFromParsingName to instantly resolve the application tile 
     * and extracts its icon via IShellItemImageFactory.
     * 
     * @param aumid The Application User Model ID of the UWP app.
     * @param outPixels Populated with the raw 32bpp BGRA (premultiplied-alpha) icon pixel data.
     * @param outWidth Populated with the icon width in pixels (typically 32).
     * @param outHeight Populated with the icon height in pixels (typically 32).
     * @return true if extraction succeeded, false otherwise.
     */
    static bool GetUwpIconByAumid(const std::wstring& aumid, std::vector<uint8_t>& outPixels, int& outWidth, int& outHeight);

private:
    /**
     * @brief Extracts a 32bpp icon bitmap for a Win32 executable and appends it to the provided vector.
     * @param results The vector to append the discovered app to.
     * @param exePath Full path to the .exe file.
     * @param displayName Friendly display name for the entry.
     */
    void ExtractWin32Icon(std::vector<ShortcutData>& results, const std::wstring& exePath, const std::wstring& displayName);

    /**
     * @brief Extracts a 32bpp icon bitmap for a UWP/packaged app shell item and appends to the provided vector.
     * @param results The vector to append the discovered app to.
     * @param pItem The IShellItem representing the UWP app tile.
     * @param displayName Friendly display name for the entry.
     * @param aumid The Application User Model ID of the packaged app.
     */
    void ExtractUwpIcon(std::vector<ShortcutData>& results, IShellItem* pItem, const std::wstring& displayName, const std::wstring& aumid);

    /**
     * @brief Helper to convert an HBITMAP into a raw 32-bit BGRA pixel array (premultiplied-alpha).
     */
    static bool HBitmapToPixels(HBITMAP hBitmap, int width, int height, std::vector<uint8_t>& outPixels);

private:
    IImageList* _imageList32; ///< System image list for Win32 icon extraction.
};
