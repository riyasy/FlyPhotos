// version.h
//
// Shared version/publisher strings for the native binaries' VERSIONINFO resources.
// Bump the numbers here only -- every .rc in the tree includes this file, so the
// three native binaries can never drift apart or from each other.
//
// Keep FLY_VER_* in sync with <AssemblyVersion> in FlyPhotos/FlyPhotos.csproj.
//
// FLY_COMPANY must match the subject name on the code-signing certificate once
// signing is in place: a publisher string that disagrees with the signature is
// worse than no publisher string at all.

#pragma once

#define FLY_VER_MAJOR 2
#define FLY_VER_MINOR 7
#define FLY_VER_PATCH 2
#define FLY_VER_BUILD 0

#define FLY_VER_STRING "2.7.2.0"

#define FLY_COMPANY   "RYFTools"
#define FLY_PRODUCT   "FlyPhotos"
#define FLY_COPYRIGHT "Copyright (C) 2026 RYFTools"
