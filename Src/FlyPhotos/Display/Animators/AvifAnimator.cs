using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;

namespace FlyPhotos.Display.Animators;

/// <summary>
///     Real-time animator for animated AVIF/HEIF files, implementing <see cref="IAnimator" />.
///     This uses the native C++ library stateful context to decode frames directly into a pre-allocated buffer
///     to ensure 0-allocation rendering during playback.
/// </summary>
public class AvifAnimator : IAnimator
{
    // --- Native C-style exports from FlyNativeLibHeif.dll ---
    /// <summary>
    /// Nested class to wrap all P/Invoke declarations to the FlyNativeLibHeif C++ DLL.
    /// </summary>
    private static class AvifNative
    {
        private const string DllName = "FlyNativeLibHeif.dll";

        /// <summary>
        /// Opens an AVIF animation given its pinned memory address and size.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr OpenAvifAnimation(IntPtr data, nint size);

        /// <summary>
        /// Returns whether the provided handle contains a sequence track.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool IsAvifAnimated(IntPtr handle);

        /// <summary>Gets the pixel width of the canvas for the animation sequence.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetAvifCanvasWidth(IntPtr handle);

        /// <summary>Gets the pixel height of the canvas for the animation sequence.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetAvifCanvasHeight(IntPtr handle);

        /// <summary>
        /// Decodes the next frame from the track into the provided `outBgraBuffer`.
        /// Returns the duration of the decoded frame in MS, or 0 on EOF/error.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int DecodeNextAvifFrame(IntPtr handle, [In, Out] byte[] outBgraBuffer);

        /// <summary>Resets the internal decoder to the beginning of the sequence to allow looping.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ResetAvifAnimation(IntPtr handle);

        /// <summary>Frees the unmanaged `AnimatedAvifReader` memory from C++.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CloseAvifAnimation(IntPtr handle);
    }

    /// <summary>Handle to the unmanaged <c>AnimatedAvifReader</c> C++ instance.</summary>
    private IntPtr _nativeHandle;

    /// <summary>Reusable pixel buffer populated by the native decoder.</summary>
    private readonly byte[] _pixelBuffer;

    /// <summary>Pinned handle to prevent GC from migrating the file memory during native usage.</summary>
    private GCHandle _pinnedFileData;

    /// <summary>The final composited Win2D GPU surface where the frame is drawn.</summary>
    private readonly CanvasRenderTarget _compositedSurface;

    /// <summary>Reused GPU texture to prevent thrashing. Loaded from <see cref="_pixelBuffer"/> per frame.</summary>
    private readonly CanvasBitmap _frameBitmap;

    /// <summary>The timestamp of the last rendering loop iteration.</summary>
    private TimeSpan _lastElapsedTime = TimeSpan.Zero;

    /// <summary>Accumulated time in milliseconds since the last frame swap.</summary>
    private double _accumulatedTimeMs = 0;

    /// <summary>Duration of the currently displayed frame in milliseconds.</summary>
    private int _currentFrameDurationMs = 0;

    /// <summary>Flag indicating whether the very first frame needs to be drawn.</summary>
    private bool _isFirstFrame = true;

    /// <summary>The width of the animation canvas in pixels.</summary>
    public uint PixelWidth { get; }

    /// <summary>The height of the animation canvas in pixels.</summary>
    public uint PixelHeight { get; }

    /// <summary>The current frame's composited surface, ready for rendering on-screen.</summary>
    public ICanvasImage Surface => _compositedSurface;

    /// <summary>
    /// Initializes a new instance of the <see cref="AvifAnimator"/> class.
    /// Private constructor. Use <see cref="CreateAsync"/> to instantiate.
    /// </summary>
    /// <param name="handle">The unmanaged handle to the native C++ decoder context.</param>
    /// <param name="canvas">The Win2D Canvas control associated with the UI.</param>
    private AvifAnimator(IntPtr handle, CanvasControl canvas)
    {
        _nativeHandle = handle;
        PixelWidth = (uint)AvifNative.GetAvifCanvasWidth(handle);
        PixelHeight = (uint)AvifNative.GetAvifCanvasHeight(handle);

        // Pre-allocate the single pixel buffer for 0-allocation rendering loop
        _pixelBuffer = new byte[PixelWidth * PixelHeight * 4];

        // Allocate the single GPU texture once. We will update its pixels dynamically rather than destroying/recreating it.
        _frameBitmap = CanvasBitmap.CreateFromBytes(
            canvas.Device,
            _pixelBuffer,
            (int)PixelWidth,
            (int)PixelHeight,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);

        // Ensure initial surface is fully transparent instead of undefined
        _compositedSurface = new CanvasRenderTarget(canvas, PixelWidth, PixelHeight, 96);
        using var ds = _compositedSurface.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
    }

    /// <summary>
    /// Associates the pinned <see cref="GCHandle"/> mapping the raw AVIF byte array with this animator.
    /// </summary>
    /// <param name="pinnedHandle">The pinned handler that must be freed upon disposal.</param>
    private void SetPinnedHandle(GCHandle pinnedHandle)
    {
        _pinnedFileData = pinnedHandle;
    }

    /// <summary>
    ///     Asynchronously creates a <see cref="AvifAnimator" /> using a background thread to prevent UI freezing.
    ///     Expects the AVIF file to be pre-loaded into memory to avoid duplicate GC allocations.
    /// </summary>
    public static async Task<AvifAnimator> CreateAsync(byte[] fileData, CanvasControl canvas)
    {
        return await Task.Run(() =>
        {
            // PIN the array in memory so the .NET GC does not move it while libheif is reading from it
            GCHandle pinnedData = GCHandle.Alloc(fileData, GCHandleType.Pinned);
            try
            {
                IntPtr memoryPtr = pinnedData.AddrOfPinnedObject();
                IntPtr handle = AvifNative.OpenAvifAnimation(memoryPtr, fileData.Length);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to open Animated AVIF via native decoder from byte array.");
                }

                var animator = new AvifAnimator(handle, canvas);
                animator.SetPinnedHandle(pinnedData);
                return animator;
            }
            catch
            {
                if (pinnedData.IsAllocated)
                    pinnedData.Free();
                throw;
            }
        });
    }

    /// <summary>
    ///     Checks quickly if an AVIF/HEIF pre-loaded byte array contains an animation sequence via native context parsing.
    /// </summary>
    public static bool IsAnimated(byte[] fileData)
    {
        if (fileData == null || fileData.Length == 0) return false;

        GCHandle pinnedData = GCHandle.Alloc(fileData, GCHandleType.Pinned);
        try
        {
            IntPtr memoryPtr = pinnedData.AddrOfPinnedObject();

            IntPtr handle = AvifNative.OpenAvifAnimation(memoryPtr, fileData.Length);
            if (handle == IntPtr.Zero) 
            {
                return false;
            }

            bool isAnimated = AvifNative.IsAvifAnimated(handle);
            AvifNative.CloseAvifAnimation(handle);
            
            return isAnimated;
        }
        finally
        {
            if (pinnedData.IsAllocated)
                pinnedData.Free();
        }
    }

    /// <summary>
    /// Decodes and advances the animation forward based on the elapsed time.
    /// Synchronously interacts with the native library, as this is invoked on Win2D's dedicated dispatcher.
    /// </summary>
    /// <param name="totalElapsedTime">The total running time of the animation loop.</param>
    /// <returns>A completed Task.</returns>
    public Task UpdateAsync(TimeSpan totalElapsedTime)
    {
        if (_nativeHandle == IntPtr.Zero) return Task.CompletedTask;

        if (_isFirstFrame)
        {
            // Win2D Update loop runs on its own background thread, so synchronous C++ decoding here is perfectly safe and
            // eliminates the GC pressure of allocating a Task/Closure per frame.
            _currentFrameDurationMs = AvifNative.DecodeNextAvifFrame(_nativeHandle, _pixelBuffer);
            if (_currentFrameDurationMs > 0)
                RenderBufferToSurface();
            _isFirstFrame = false;
            _lastElapsedTime = totalElapsedTime;
            return Task.CompletedTask;
        }

        var delta = totalElapsedTime - _lastElapsedTime;
        _lastElapsedTime = totalElapsedTime;

        if (delta < TimeSpan.Zero)
        {
            // Clock reset (e.g. animation was restarted)
            AvifNative.ResetAvifAnimation(_nativeHandle);
            _currentFrameDurationMs = AvifNative.DecodeNextAvifFrame(_nativeHandle, _pixelBuffer);
            if (_currentFrameDurationMs > 0) RenderBufferToSurface();
            _accumulatedTimeMs = 0;
            return Task.CompletedTask;
        }

        _accumulatedTimeMs += delta.TotalMilliseconds;

        bool rendered = false;
        while (_accumulatedTimeMs >= _currentFrameDurationMs && _currentFrameDurationMs > 0)
        {
            _accumulatedTimeMs -= _currentFrameDurationMs;

            int nextDurationMs = AvifNative.DecodeNextAvifFrame(_nativeHandle, _pixelBuffer);
            if (nextDurationMs <= 0)
            {
                // Sequence finished, loop back to the beginning
                AvifNative.ResetAvifAnimation(_nativeHandle);
                nextDurationMs = AvifNative.DecodeNextAvifFrame(_nativeHandle, _pixelBuffer);
                if (nextDurationMs <= 0) break; // Error playing
            }

            _currentFrameDurationMs = nextDurationMs;
            rendered = true;
        }

        if (rendered)
            RenderBufferToSurface();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Transmits the latest natively decoded <see cref="_pixelBuffer"/> bytes up to the GPU texture
    /// and renders it cleanly onto the <see cref="_compositedSurface"/>.
    /// </summary>
    private void RenderBufferToSurface()
    {
        if (_nativeHandle == IntPtr.Zero || _compositedSurface.Device == null) return;

        // Upload the new pixel data directly to the existing GPU texture (No GC allocation, No DX thrashing)
        _frameBitmap.SetPixelBytes(_pixelBuffer);

        // Stamp the fully composited frame directly onto the surface
        using var ds = _compositedSurface.CreateDrawingSession();
        ds.Blend = CanvasBlend.Copy;
        ds.DrawImage(_frameBitmap);
    }

    /// <summary>
    /// Disposes the native memory context, GPU textures, and safely unpins the C# file data byte array mapping.
    /// </summary>
    public void Dispose()
    {
        if (_nativeHandle != IntPtr.Zero)
        {
            AvifNative.CloseAvifAnimation(_nativeHandle);
            _nativeHandle = IntPtr.Zero;
        }

        if (_pinnedFileData.IsAllocated)
        {
            _pinnedFileData.Free();
        }

        _frameBitmap?.Dispose();
        _compositedSurface?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer to ensure unmanaged memory is cleaned up if <see cref="Dispose"/> is forgotten.
    /// </summary>
    ~AvifAnimator()
    {
        Dispose();
    }
}