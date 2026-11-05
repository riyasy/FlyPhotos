// dllmain.cpp : Defines the entry point for the DLL application.
// DirectXTex needs no global init/deinit, so unlike FlyNativeLibHeif there is nothing to bracket here.
#include "pch.h"

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    if (ul_reason_for_call == DLL_PROCESS_ATTACH)
        DisableThreadLibraryCalls(hModule);
    return TRUE;
}
