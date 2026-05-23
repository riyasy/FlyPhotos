#pragma once
#include <Windows.h>
#include <libheif/heif.h>

// Called once from DllMain DLL_PROCESS_ATTACH / DLL_PROCESS_DETACH.
// heif_init / heif_deinit must bracket the entire DLL lifetime, not individual decodes.
// Putting these calls inside HeifReader (per-decode) caused heif_deinit to tear down
// codec/plugin state (e.g. dav1d) while AnimatedAvifReader still held live contexts.
void DllHeifInit();
void DllHeifDeinit();
