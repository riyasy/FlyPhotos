using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;

namespace FlyPhotos.Display.Animators;

/// <summary>
///     Real-time animator for animated AVIF/HEIF files, implementing <see cref="IAnimator" />.
///     This uses the native C++ library stateful context to decode frames directly into a pre-allocated buffer
///     to ensure 0-allocation rendering during playback.
/// </summary>
public partial class AvifAnimator : IAnimator
{
    /// <summary>Handle to the unmanaged <c>AnimatedAvifReader</c> C++ instance.</summary>
    private IntPtr _nativeHandle;

    /// <summary>Pointer to the unmanaged memory holding the raw AVIF file bytes.</summary>
    private IntPtr _unmanagedFileData;

    /// <summary>Reusable pixel buffer populated by the native decoder.</summary>
    private readonly byte[] _pixelBuffer;

    /// <summary>The final composited Win2D GPU surface where the frame is drawn.</summary>
    private readonly CanvasRenderTarget _compositedSurface;

    /// <summary>Reused GPU texture to prevent thrashing. Loaded from <see cref="_pixelBuffer" /> per frame.</summary>
    private readonly CanvasBitmap _frameBitmap;

    /// <summary>Cached bounds for drawing without DPI scaling blur.</summary>
    private readonly Rect _canvasRect;

    /// <summary>The timestamp of the last rendering loop iteration.</summary>
    private TimeSpan _lastElapsedTime = TimeSpan.Zero;

    /// <summary>Accumulated time in milliseconds since the last frame swap.</summary>
    private double _accumulatedTimeMs = 0;

    /// <summary>Duration of the currently displayed frame in milliseconds.</summary>
    private int _currentFrameDurationMs = 0;

    /// <summary>Flag indicating whether the very first frame needs to be drawn.</summary>
    private bool _isFirstFrame = true;

    /// <summary>Tracks if Dispose has already been called.</summary>
    private bool _isDisposed = false;

    /// <summary>The width of the animation canvas in pixels.</summary>
    public uint PixelWidth { get; }

    /// <summary>The height of the animation canvas in pixels.</summary>
    public uint PixelHeight { get; }

    /// <summary>The current frame's composited surface, ready for rendering on-screen.</summary>
    public ICanvasImage Surface => _compositedSurface;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AvifAnimator" /> class.
    ///     Private constructor. Use <see cref="CreateAsync" /> to instantiate.
    /// </summary>
    private AvifAnimator(IntPtr handle, IntPtr unmanagedFileData, CanvasControl canvas)
    {
        _nativeHandle = handle;
        _unmanagedFileData = unmanagedFileData;

        PixelWidth = (uint)NativeAvifBridge.GetAvifCanvasWidth(handle);
        PixelHeight = (uint)NativeAvifBridge.GetAvifCanvasHeight(handle);

        _canvasRect = new Rect(0, 0, PixelWidth, PixelHeight);

        // Pre-allocate the single pixel buffer for 0-allocation rendering loop
        _pixelBuffer = new byte[PixelWidth * PixelHeight * 4];

        // Allocate the single GPU texture once. We will update its pixels dynamically rather than destroying/recreating it.
        _frameBitmap = CanvasBitmap.CreateFromBytes(canvas.Device, _pixelBuffer,
            (int)PixelWidth, (int)PixelHeight, DirectXPixelFormat.B8G8R8A8UIntNormalized);

        // Ensure initial surface is fully transparent instead of undefined
        _compositedSurface = new CanvasRenderTarget(canvas, PixelWidth, PixelHeight, 96);
        using var ds = _compositedSurface.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
    }

    /// <summary>
    ///     Asynchronously creates a <see cref="AvifAnimator" /> using a background thread to prevent UI freezing.
    /// </summary>
    public static async Task<AvifAnimator> CreateAsync(byte[] fileData, CanvasControl canvas)
    {
        return await Task.Run(() =>
        {
            // Allocate native memory and copy the array over.
            // This prevents long-term GC pinning, allowing the .NET garbage collector
            // to immediately clean up the original byte array and prevent heap fragmentation.
            IntPtr unmanagedMemory = Marshal.AllocHGlobal(fileData.Length);
            try
            {
                Marshal.Copy(fileData, 0, unmanagedMemory, fileData.Length);

                IntPtr handle = NativeAvifBridge.OpenAvifAnimation(unmanagedMemory, fileData.Length);
                if (handle == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to open Animated AVIF via native decoder from byte array.");

                try
                {
                    return new AvifAnimator(handle, unmanagedMemory, canvas);
                }
                catch
                {
                    NativeAvifBridge.CloseAvifAnimation(handle);
                    throw;
                }
            }
            catch
            {
                // If anything fails during setup, free the native memory immediately
                if (unmanagedMemory != IntPtr.Zero)
                    Marshal.FreeHGlobal(unmanagedMemory);
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
        // For a quick, synchronous check, pinning is perfectly fine because it only lasts a few milliseconds.
        GCHandle pinnedData = GCHandle.Alloc(fileData, GCHandleType.Pinned);
        try
        {
            IntPtr memoryPtr = pinnedData.AddrOfPinnedObject();
            IntPtr handle = NativeAvifBridge.OpenAvifAnimation(memoryPtr, fileData.Length);
            if (handle == IntPtr.Zero)
                return false;

            try
            {
                return NativeAvifBridge.IsAvifAnimated(handle);
            }
            finally
            {
                // Ensure the handle is always closed, even if IsAvifAnimated throws
                NativeAvifBridge.CloseAvifAnimation(handle);
            }
        }
        finally
        {
            if (pinnedData.IsAllocated)
                pinnedData.Free();
        }
    }

    /// <summary>
    ///     Decodes and advances the animation forward based on the elapsed time.
    ///     Synchronously interacts with the native library, as this is invoked on Win2D's dedicated dispatcher.
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
            _currentFrameDurationMs = NativeAvifBridge.DecodeNextAvifFrame(_nativeHandle, _pixelBuffer);
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
            NativeAvifBridge.ResetAvifAnimation(_nativeHandle);
            _currentFrameDurationMs = NativeAvifBridge.DecodeNextAvifFrame(_nativeHandle, _pixelBuffer);
            if (_currentFrameDurationMs > 0) RenderBufferToSurface();
            _accumulatedTimeMs = 0;
            return Task.CompletedTask;
        }

        _accumulatedTimeMs += delta.TotalMilliseconds;

        bool rendered = false;

        // Accumulate intermediate frames without drawing them to Win2D if the loop is running behind
        while (_accumulatedTimeMs >= _currentFrameDurationMs && _currentFrameDurationMs > 0)
        {
            _accumulatedTimeMs -= _currentFrameDurationMs;

            int nextDurationMs = NativeAvifBridge.DecodeNextAvifFrame(_nativeHandle, _pixelBuffer);
            if (nextDurationMs <= 0)
            {
                // Sequence finished, loop back to the beginning
                NativeAvifBridge.ResetAvifAnimation(_nativeHandle);
                nextDurationMs = NativeAvifBridge.DecodeNextAvifFrame(_nativeHandle, _pixelBuffer);
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
    ///     Transmits the latest natively decoded <see cref="_pixelBuffer" /> bytes up to the GPU texture
    ///     and renders it cleanly onto the <see cref="_compositedSurface" />.
    /// </summary>
    private void RenderBufferToSurface()
    {
        if (_nativeHandle == IntPtr.Zero || _compositedSurface.Device == null) return;

        // Upload the new pixel data directly to the existing GPU texture (No GC allocation, No DX thrashing)
        _frameBitmap.SetPixelBytes(_pixelBuffer);

        // Stamp the fully composited frame directly onto the surface
        using var ds = _compositedSurface.CreateDrawingSession();
        ds.Blend = CanvasBlend.Copy;

        // Use the Rect overload to guarantee strict 1:1 pixel mapping (Fixes DPI Scaling blur)
        ds.DrawImage(_frameBitmap, _canvasRect);
    }

    /// <summary>
    ///     Disposes the native memory context, GPU textures, and frees the unmanaged file bytes.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Internal disposal logic conforming to the standard IDisposable pattern to prevent Win2D GC finalizer crashes.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        // 1. Free Unmanaged resources (Always safe to do, even from the Finalizer thread)
        if (_nativeHandle != IntPtr.Zero)
        {
            NativeAvifBridge.CloseAvifAnimation(_nativeHandle);
            _nativeHandle = IntPtr.Zero;
        }

        if (_unmanagedFileData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_unmanagedFileData);
            _unmanagedFileData = IntPtr.Zero;
        }

        // 2. Free Managed resources (Only when explicitly disposed, NEVER from the Finalizer thread)
        if (disposing)
        {
            _frameBitmap?.Dispose();
            _compositedSurface?.Dispose();
        }

        _isDisposed = true;
    }

    /// <summary>
    ///     Finalizer to ensure unmanaged memory is cleaned up if <see cref="Dispose" /> is forgotten.
    /// </summary>
    ~AvifAnimator()
    {
        Dispose(false);
    }
}