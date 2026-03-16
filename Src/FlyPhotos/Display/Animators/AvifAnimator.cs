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
/// </summary>
/// <remarks>
///     <para>
///         <b>Native decode path.</b>
///         AVIF decoding is performed by a native C++ library accessed through
///         <see cref="NativeAvifBridge" />. The library maintains a stateful per-file context
///         (opened via <c>OpenAvifAnimation</c>) that advances through frames sequentially.
///         This is fundamentally different from the WIC-based animators (GIF, APNG, WebP),
///         which seek to arbitrary frames by index. The native context must be explicitly
///         reset (<c>ResetAvifAnimation</c>) to loop back to the beginning.
///     </para>
///     <para>
///         <b>Zero managed allocation on the hot path.</b>
///         The pixel buffer (<see cref="_pixelBuffer" />) is permanently pinned at construction
///         via a <see cref="GCHandle" />. The native decoder writes directly into it via
///         <see cref="_pixelBufferPtr" />, and Win2D uploads from it via <c>SetPixelBytes</c>.
///         <see cref="UpdateAsync" /> returns <c>Task.CompletedTask</c> — no async state machine
///         is allocated per call. The combination achieves zero managed heap allocation during
///         steady-state playback.
///     </para>
///     <para>
///         <b>Timing model.</b>
///         Unlike the index-based animators which map wall-clock time directly to a frame index,
///         this animator uses a delta-time accumulator. Each <see cref="UpdateAsync" /> call
///         measures the elapsed time since the previous call and advances through as many frames
///         as that delta covers. This approach is natural for a stateful native decoder that
///         cannot seek backwards without a full reset.
///     </para>
///     <para>
///         <b>GPU resource thread requirement.</b>
///         <see cref="CanvasBitmap" /> and <see cref="CanvasRenderTarget" /> must be created on
///         the Win2D device thread. The native CPU work (memory allocation, file copy, decoder
///         open) is offloaded to a threadpool thread in <see cref="CreateAsync" />; GPU resource
///         creation happens back on the calling thread after the threadpool task completes.
///     </para>
/// </remarks>
public partial class AvifAnimator : IAnimator
{
    // -------------------------------------------------------------------------
    // Private fields
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Handle to the native <c>AnimatedAvifReader</c> C++ object.
    ///     Zero when the animator has been disposed or the native open failed.
    ///     Zeroed after <c>CloseAvifAnimation</c> to prevent double-close.
    /// </summary>
    private IntPtr _nativeHandle;

    /// <summary>
    ///     Pointer to the unmanaged memory block holding the raw AVIF file bytes.
    ///     Allocated via <c>Marshal.AllocHGlobal</c> in <see cref="CreateAsync" /> and
    ///     kept alive for the lifetime of the animator because the native decoder reads
    ///     from it on every <c>DecodeNextAvifFrame</c> call. Freed in <see cref="Dispose(bool)" />.
    /// </summary>
    private IntPtr _unmanagedFileData;

    /// <summary>
    ///     CPU-side pixel buffer that receives decoded frame data from the native decoder.
    ///     Sized to <c>PixelWidth × PixelHeight × 4</c> bytes (BGRA8, one byte per channel).
    ///     Permanently pinned at construction so the GC never relocates it, allowing the
    ///     native decoder to write into <see cref="_pixelBufferPtr" /> without marshalling,
    ///     and allowing Win2D's <c>SetPixelBytes</c> to upload without additional pinning.
    ///     Permanent pinning of a single object is preferable to repeated transient pinning,
    ///     which causes LOH fragmentation for buffers of this size.
    /// </summary>
    private readonly byte[] _pixelBuffer;

    /// <summary>
    ///     GC pin handle for <see cref="_pixelBuffer" />.
    ///     Allocated once at construction and freed in <see cref="Dispose(bool)" /> after
    ///     the native handle is closed to ensure the native side has stopped writing.
    /// </summary>
    private GCHandle _pixelBufferPin;

    /// <summary>
    ///     Raw pointer to the start of the pinned <see cref="_pixelBuffer" />.
    ///     Passed directly to <c>DecodeNextAvifFrame</c>, eliminating the managed
    ///     array as an intermediary on the decode path.
    /// </summary>
    private readonly IntPtr _pixelBufferPtr;

    /// <summary>
    ///     Off-screen render target where each decoded frame is stamped.
    ///     Exposed as <see cref="Surface" /> for the Win2D render loop.
    /// </summary>
    private readonly CanvasRenderTarget _compositedSurface;

    /// <summary>
    ///     Reusable GPU texture updated via <c>SetPixelBytes</c> on each frame render.
    ///     Avoids the D3D texture create/destroy cycle that would occur if a new
    ///     <c>CanvasBitmap</c> were allocated per frame.
    /// </summary>
    private readonly CanvasBitmap _frameBitmap;

    /// <summary>
    ///     Pre-computed full-canvas <see cref="Rect" />.
    ///     Passed to <c>DrawImage</c> to guarantee 1:1 pixel mapping, bypassing the
    ///     DPI-scaling interpolation that Win2D's float-coordinate overload applies.
    /// </summary>
    private readonly Rect _canvasRect;

    /// <summary>
    ///     Timestamp of the most recent <see cref="UpdateAsync" /> call.
    ///     Used to compute the elapsed delta since the last render tick.
    /// </summary>
    private TimeSpan _lastElapsedTime = TimeSpan.Zero;

    /// <summary>
    ///     Accumulated wall-clock milliseconds not yet consumed by a frame advance.
    ///     Incremented by the per-tick delta and decremented by each frame's duration
    ///     as the catch-up loop advances through frames.
    /// </summary>
    private double _accumulatedTimeMs;

    /// <summary>
    ///     Display duration of the currently shown frame in milliseconds, as returned
    ///     by the most recent successful <c>DecodeNextAvifFrame</c> call.
    ///     A value of 0 indicates a stalled/error state; the catch-up loop's
    ///     <c>&amp;&amp; _currentFrameDurationMs &gt; 0</c> guard suppresses further
    ///     decode attempts until an explicit reset.
    /// </summary>
    private int _currentFrameDurationMs;

    /// <summary>
    ///     <c>true</c> until the very first frame has been decoded and rendered.
    ///     The first-frame path initialises <see cref="_lastElapsedTime" /> and
    ///     <see cref="_currentFrameDurationMs" /> before the delta accumulator begins.
    /// </summary>
    private bool _isFirstFrame = true;

    /// <summary>Guards against double-disposal.</summary>
    private bool _isDisposed;

    // -------------------------------------------------------------------------
    // IAnimator public surface
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public uint PixelWidth { get; }

    /// <inheritdoc />
    public uint PixelHeight { get; }

    /// <inheritdoc />
    public ICanvasImage Surface => _compositedSurface;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Private constructor. Use <see cref="CreateAsync" /> to instantiate.
    ///     Performs only GPU resource creation — no native decode work happens here.
    ///     Must be called on the Win2D device thread (not inside <c>Task.Run</c>).
    /// </summary>
    private AvifAnimator(IntPtr handle, IntPtr unmanagedFileData, CanvasControl canvas)
    {
        _nativeHandle = handle;
        _unmanagedFileData = unmanagedFileData;

        PixelWidth = (uint)NativeAvifBridge.GetAvifCanvasWidth(handle);
        PixelHeight = (uint)NativeAvifBridge.GetAvifCanvasHeight(handle);
        _canvasRect = new Rect(0, 0, PixelWidth, PixelHeight);

        _pixelBuffer = new byte[PixelWidth * PixelHeight * 4];
        _pixelBufferPin = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);
        _pixelBufferPtr = _pixelBufferPin.AddrOfPinnedObject();

        // GPU resources created here, on the Win2D device thread. Creating them inside
        // Task.Run (threadpool) would race against the device's dispatcher queue.
        _frameBitmap = CanvasBitmap.CreateFromBytes(
            canvas.Device, _pixelBuffer,
            (int)PixelWidth, (int)PixelHeight,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);

        _compositedSurface = new CanvasRenderTarget(canvas, PixelWidth, PixelHeight, 96);
        using var ds = _compositedSurface.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
    }

    /// <summary>
    ///     Asynchronously creates a <see cref="AvifAnimator" /> from raw AVIF file bytes.
    /// </summary>
    /// <remarks>
    ///     CPU-bound work (native memory allocation, file data copy, native decoder open) is
    ///     offloaded to a threadpool thread. GPU resource creation (constructor body) runs on
    ///     the calling thread after the threadpool task completes, satisfying Win2D's requirement
    ///     that GPU objects be created on the device thread.
    /// </remarks>
    /// <param name="fileData">Complete raw bytes of the AVIF file.</param>
    /// <param name="canvas">The Win2D <see cref="CanvasControl" /> that owns the GPU device.</param>
    /// <returns>A fully initialised <see cref="AvifAnimator" /> ready for <see cref="UpdateAsync" /> calls.</returns>
    public static async Task<AvifAnimator> CreateAsync(byte[] fileData, CanvasControl canvas)
    {
        // Phase 1 (threadpool): native allocation and decoder open — CPU-only work.
        // The file bytes are copied into unmanaged memory so the native decoder can hold a
        // raw pointer without pinning the managed array, allowing the GC to compact the heap.
        var (handle, unmanagedMemory) = await Task.Run(() =>
        {
            IntPtr mem = Marshal.AllocHGlobal(fileData.Length);
            try
            {
                Marshal.Copy(fileData, 0, mem, fileData.Length);

                IntPtr h = NativeAvifBridge.OpenAvifAnimation(mem, fileData.Length);
                if (h == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to open animated AVIF via native decoder.");

                return (h, mem);
            }
            catch
            {
                Marshal.FreeHGlobal(mem);
                throw;
            }
        });

        // Phase 2 (calling thread): GPU resource creation.
        // unmanagedMemory is owned by this scope until the constructor takes it.
        // If the constructor throws, both the native handle and unmanaged memory are
        // cleaned up here; there is no other scope that holds a copy of the pointer.
        try
        {
            return new AvifAnimator(handle, unmanagedMemory, canvas);
        }
        catch
        {
            NativeAvifBridge.CloseAvifAnimation(handle);
            Marshal.FreeHGlobal(unmanagedMemory);
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Animation update loop
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Advances the animation by the elapsed time since the previous call and renders
    ///     the resulting frame to <see cref="_compositedSurface" />.
    ///     Fully synchronous — returns <c>Task.CompletedTask</c> with no async state machine.
    /// </summary>
    /// <param name="totalElapsedTime">Total time elapsed since the animator was started.</param>
    public Task UpdateAsync(TimeSpan totalElapsedTime)
    {
        if (_nativeHandle == IntPtr.Zero) return Task.CompletedTask;

        // First-frame path: initialise timing state and render frame 0.
        if (_isFirstFrame)
        {
            _currentFrameDurationMs = NativeAvifBridge.DecodeNextAvifFrame(_nativeHandle, _pixelBufferPtr);
            if (_currentFrameDurationMs > 0)
                RenderBufferToSurface();
            _isFirstFrame = false;
            _lastElapsedTime = totalElapsedTime;
            return Task.CompletedTask;
        }

        var delta = totalElapsedTime - _lastElapsedTime;
        _lastElapsedTime = totalElapsedTime;

        // Negative delta means the caller's clock was reset (e.g. explicit restart).
        // Reset the native decoder back to frame 0 and restart accumulation.
        if (delta < TimeSpan.Zero)
        {
            NativeAvifBridge.ResetAvifAnimation(_nativeHandle);
            _currentFrameDurationMs = NativeAvifBridge.DecodeNextAvifFrame(_nativeHandle, _pixelBufferPtr);
            if (_currentFrameDurationMs > 0) RenderBufferToSurface();
            else _currentFrameDurationMs = 0;
            _accumulatedTimeMs = 0;
            return Task.CompletedTask;
        }

        // Cap the accumulator before adding the new delta. Without this cap, a long stall
        // (e.g. app backgrounded) would cause a burst of synchronous native decodes on the
        // next tick, potentially freezing the render thread for a noticeable duration.
        // 10 frames of catch-up per tick is enough to stay smooth under moderate frame drops.
        const int maxCatchUpFrames = 10;
        if (_currentFrameDurationMs > 0)
        {
            double maxAccumulated = _currentFrameDurationMs * maxCatchUpFrames;
            if (_accumulatedTimeMs > maxAccumulated)
                _accumulatedTimeMs = maxAccumulated;
        }

        _accumulatedTimeMs += delta.TotalMilliseconds;

        bool rendered = false;

        while (_accumulatedTimeMs >= _currentFrameDurationMs && _currentFrameDurationMs > 0)
        {
            _accumulatedTimeMs -= _currentFrameDurationMs;

            int nextDurationMs = NativeAvifBridge.DecodeNextAvifFrame(_nativeHandle, _pixelBufferPtr);
            if (nextDurationMs <= 0)
            {
                // Native decoder signals end-of-sequence. Reset to loop back to frame 0.
                // Accumulated time is also reset so the loop boundary does not carry overshoot
                // into the first frame of the new cycle, which would skip it immediately.
                NativeAvifBridge.ResetAvifAnimation(_nativeHandle);
                _accumulatedTimeMs = 0;

                nextDurationMs = NativeAvifBridge.DecodeNextAvifFrame(_nativeHandle, _pixelBufferPtr);
                if (nextDurationMs <= 0)
                {
                    // Post-reset decode also failed. Zero out the duration to suppress the
                    // while guard for all remaining iterations this tick and every subsequent
                    // tick, preventing a native call storm against a persistently broken file.
                    _currentFrameDurationMs = 0;
                    break;
                }
            }

            _currentFrameDurationMs = nextDurationMs;
            rendered = true;
        }

        if (rendered)
            RenderBufferToSurface();

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Uploads the latest decoded pixels from <see cref="_pixelBuffer" /> to
    ///     <see cref="_frameBitmap" /> and stamps it onto <see cref="_compositedSurface" />.
    /// </summary>
    private void RenderBufferToSurface()
    {
        if (_nativeHandle == IntPtr.Zero || _compositedSurface.Device == null) return;

        // SetPixelBytes reads from _pixelBuffer. Because the buffer is permanently pinned,
        // no additional GC pin/unpin occurs here — zero GC pressure on this call.
        _frameBitmap.SetPixelBytes(_pixelBuffer);

        using var ds = _compositedSurface.CreateDrawingSession();
        ds.Blend = CanvasBlend.Copy;

        // Rect overload guarantees 1:1 pixel mapping — bypasses Win2D's DPI-scaling transform.
        ds.DrawImage(_frameBitmap, _canvasRect);
    }

    // -------------------------------------------------------------------------
    // IDisposable / finalizer
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Internal disposal logic following the standard IDisposable/finalizer pattern.
    /// </summary>
    /// <param name="disposing">
    ///     <c>true</c> when called from <see cref="Dispose()" /> — safe to release managed resources.<br />
    ///     <c>false</c> when called from the finalizer — managed objects may already be collected;
    ///     only unmanaged resources are released.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        // Unmanaged resources — always safe to release, including from the finalizer thread.
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

        // Release the GC pin after closing the native handle, ensuring the native side
        // has stopped writing into _pixelBuffer before we allow the GC to move it.
        if (_pixelBufferPin.IsAllocated)
            _pixelBufferPin.Free();

        // Managed Win2D GPU resources — only safe to release when explicitly disposed.
        // Releasing Win2D objects from a finalizer thread races against the device dispatcher.
        if (disposing)
        {
            _frameBitmap?.Dispose();
            _compositedSurface?.Dispose();
        }

        _isDisposed = true;
    }

    /// <summary>
    ///     Finalizer backstop ensuring native memory and handles are released even if
    ///     <see cref="Dispose()" /> is never called. Win2D GPU resources are NOT released
    ///     from here — see <see cref="Dispose(bool)" />.
    /// </summary>
    ~AvifAnimator()
    {
        Dispose(false);
    }
}