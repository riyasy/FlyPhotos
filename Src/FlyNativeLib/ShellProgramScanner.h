/**
 * @file ShellProgramScanner.h
 * @brief Declares ShellProgramScanner - helper to enumerate Start Menu shortcuts and extract icons.
 *
 * This helper scans well-known Start Menu locations to find ".lnk" shortcut files, resolves
 * them to their target executables and extracts a 32bpp BGRA icon bitmap for display in
 * UI lists. It is implemented using Shell APIs and is intended to be consumed by the
 * native helper library exported to managed code.
 */

#pragma once

#include <windows.h>
#include <shobjidl.h>
#include <vector>
#include <string>

using namespace std;

/**
 * @brief Holds information about a discovered Start Menu shortcut / program.
 *
 * Name        - Display name of the shortcut (derived from the .lnk filename or target)
 * Path        - Full path to the shortcut file (or resolved executable path when available)
 * Width/Height - Dimensions of the bitmap stored in Pixels (typically 32x32 or 48x48)
 * Pixels      - Raw BGRA (32bpp) pixel data for the icon image.
 */
struct ShortcutData
{
    std::wstring Name;
    std::wstring Path;
    int Width;
    int Height;
    std::vector<uint8_t> Pixels; // BGRA 32bpp
};

/**
 * @brief Scans Windows Start Menu folders for installed Win32 programs and their icons.
 *
 * ShellProgramScanner provides a small, focused API to enumerate .lnk files below the
 * Start Menu folders (per-user and all-users), resolve the link to the underlying
 * executable and extract an icon bitmap suitable for display in a list UI.
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
     * @brief Constructs a ShellProgramScanner instance and initializes internal COM helpers.
     */
    ShellProgramScanner();

    /**
     * @brief Destructor - releases any COM objects and allocated resources.
     */
    ~ShellProgramScanner();

    /**
     * @brief Performs the scan of Start Menu directories and populates internal results.
     *
     * This is a synchronous operation that will walk the filesystem under the Start Menu
     * locations, attempt to resolve .lnk shortcuts and extract an icon bitmap for each
     * resolved executable. Errors are swallowed per-entry to allow best-effort enumeration.
     */
    void Scan();

    /**
     * @brief Returns the vector of discovered ShortcutData entries.
     * @return const reference to internal results vector.
     */
    const std::vector<ShortcutData>& GetResults() const;

private:
    /**
     * @brief Recursively scan the provided folder path for .lnk files.
     * @param folderPath Full path to a folder to scan.
     */
    void ScanDirectory(const std::wstring& folderPath);

    /**
     * @brief Resolve a .lnk shortcut to its target path and attempt to extract an icon.
     * @param lnkPath Full path to the .lnk file to resolve.
     */
    void ResolveLink(const std::wstring& lnkPath);

    /**
     * @brief Extract a representative icon bitmap from the specified executable.
     * @param exePath Full path to the executable file.
     * @param displayName Friendly display name to use when creating the ShortcutData entry.
     */
    void ExtractIcon(const std::wstring& exePath, const std::wstring& displayName);

private:

    IShellLinkW* _psl; // Pointer to a COM IShellLinkW instance used to load and resolve .lnk shortcuts.
    IPersistFile* _ppf; // Pointer to IPersistFile used alongside IShellLinkW to load .lnk files.
    IImageList* _imageList32; // COM image list (IImageList) for retrieving extracted icon bitmaps.
    std::vector<ShortcutData> _results; // Container for results discovered by Scan().
};
