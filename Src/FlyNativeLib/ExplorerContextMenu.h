#pragma once
#include <ShlObj.h>

class ExplorerContextMenu
{
public:
    // Shows a fully functional context menu for the given file path at the specified screen coordinates.
    // Returns true if the menu was shown, false on any failure.
    static bool ShowContextMenu(HINSTANCE appInstance, HWND ownerHwnd, LPCWSTR filePath, int posX, int posY);

private:
    struct MenuContext
    {
        IContextMenu2* pICM2 = nullptr;
        IContextMenu3* pICM3 = nullptr;
    };

    static LRESULT CALLBACK WndProc(HWND hWnd, UINT message, WPARAM wParam, LPARAM lParam);
    static HWND CreateHiddenWindow(HINSTANCE appInstance, MenuContext* pContext);

    static const wchar_t CLASS_NAME[];
    static bool s_classRegistered;
};
