// pch.h: This is a precompiled header file.
// Files listed below are compiled only once, improving build performance for future builds.
// This also affects IntelliSense performance, including code completion and many code browsing features.
// However, files listed here are ALL re-compiled if any one of them is updated between builds.
// Do not add files here that you will be updating frequently as this negates the performance advantage.

#ifndef PCH_H
#define PCH_H

// Define the minimum required platform.
// This must be done BEFORE including windows.h or any other system header.
// 0x0601 = Windows 7 and later
// 0x0A00 = Windows 10 and later
// Choose a value appropriate for your minimum target OS.
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601 
#endif

// add headers that you want to pre-compile here
#include "framework.h"

#endif //PCH_H
