@echo off

REM Base directory = current script location
set BASE_DIR=%~dp0

REM Source paths
set X64_SRC=%BASE_DIR%target\x86_64-pc-windows-msvc\release\fly_rust_bridge.dll
set ARM64_SRC=%BASE_DIR%target\aarch64-pc-windows-msvc\release\fly_rust_bridge.dll

REM Destination paths (relative)
set X64_DST=%BASE_DIR%..\FlyPhotos\External\x64\
set ARM64_DST=%BASE_DIR%..\FlyPhotos\External\ARM64\

echo Copying x64 DLL...
copy /Y "%X64_SRC%" "%X64_DST%"

echo Copying ARM64 DLL...
copy /Y "%ARM64_SRC%" "%ARM64_DST%"

echo Done.
pause