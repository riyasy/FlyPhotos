#pragma once
#include <ShlObj.h>


class ExplorerContextMenu
{
public:
    bool ShowContextMenu(HINSTANCE appInstance, LPCWSTR filePath, int posX, int posY);

private:
    HWND CreateHiddenWindow(HINSTANCE appInstance);
    static LRESULT CALLBACK WndProc(HWND hWnd, UINT message, WPARAM wParam, LPARAM lParam);

    static IContextMenu2* s_pICM2;
    static IContextMenu3* s_pICM3;

    static bool s_classRegistered;
    static const wchar_t CLASS_NAME[];

    static void CleanUp();
};


