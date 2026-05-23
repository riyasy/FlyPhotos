// dllmain.cpp : Defines the entry point for the DLL application.
#include "pch.h"
#include "DllGlobals.h"

HINSTANCE g_hInst = NULL;

void DllHeifInit()  { heif_init(nullptr); }
void DllHeifDeinit(){ heif_deinit(); }

static BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
                     )
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        g_hInst = hModule;
        DllHeifInit();
        break;
    case DLL_PROCESS_DETACH:
        DllHeifDeinit();
        break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
        break;
    }
    return TRUE;
}

