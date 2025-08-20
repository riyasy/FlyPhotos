using System;
using System.Reflection;
using System.Runtime.InteropServices;
using LibHeifSharp;

namespace FlyPhotos.Readers;

internal static class LibHeifSharpDllImportResolver
{
    private static IntPtr _cachedLibHeifModule = IntPtr.Zero;
    private static bool _firstRequestForLibHeif = true;

    /// <summary>
    /// Registers the <see cref="DllImportResolver"/> for the LibHeifSharp assembly.
    /// </summary>
    public static void Register()
    {
        // The runtime will execute the specified callback when it needs to resolve a native library
        // import for the LibHeifSharp assembly.
        NativeLibrary.SetDllImportResolver(typeof(LibHeifInfo).Assembly, Resolver);
    }

    private static IntPtr Resolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // We only care about a native library named libheif, the runtime will use
        // its default behavior for any other native library.
        if (!string.Equals(libraryName, "libheif", StringComparison.Ordinal)) return IntPtr.Zero;
        // Because the DllImportResolver will be called multiple times we load libheif once
        // and cache the module handle for future requests.
        if (!_firstRequestForLibHeif) return _cachedLibHeifModule;
        _firstRequestForLibHeif = false;
        _cachedLibHeifModule = LoadNativeLibrary(libraryName, assembly, searchPath);
        return _cachedLibHeifModule;
        // Fall back to default import resolver.
    }

    private static nint LoadNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (OperatingSystem.IsWindows())
            // On Windows the libheif DLL name defaults to heif.dll, so we try to load that if
            // libheif.dll was not found.
            try
            {
                return NativeLibrary.Load(libraryName, assembly, searchPath);
            }
            catch (DllNotFoundException)
            {
                if (NativeLibrary.TryLoad("heif.dll", assembly, searchPath, out var handle))
                    return handle;
                throw;
            }
        //else if (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsWatchOS())
        //{
        //    // The Apple mobile/embedded platforms statically link libheif into the AOT compiled main program binary.
        //    return NativeLibrary.GetMainProgramHandle();
        //}

        // Use the default runtime behavior for all other platforms.
        return NativeLibrary.Load(libraryName, assembly, searchPath);
    }
}