#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.UI;
using Microsoft.Graphics.Canvas;
using WinRT;

namespace FlyPhotos.Infra.Interop;

/// <summary>
/// An open PDF: the Windows.Data.Pdf document and the file under it. The file is opened with delete and write
/// sharing, so it can be renamed or deleted while it is on screen. Dispose to close the file: the document holds
/// its stream until a GC, so the handle must close independently of it.
/// </summary>
internal sealed partial class PdfFile : IDisposable
{
    /// <summary>The file, read by the document for as long as it is open.</summary>
    private readonly ClosableStream.Opened _stream;

    /// <summary>The file's last write time and length when opened, to notice it being overwritten or replaced.</summary>
    private readonly (DateTime Written, long Length) _stamp;

    /// <summary>The parsed document. Parsing is lazy: opening costs the same for 1 page or 1,000.</summary>
    public PdfDocument Document { get; }

    /// <summary>Use <see cref="OpenAsync"/>.</summary>
    private PdfFile(ClosableStream.Opened stream, (DateTime, long) stamp, PdfDocument document) =>
        (_stream, _stamp, Document) = (stream, stamp, document);

    /// <summary>
    /// Whether a file is at <paramref name="path"/> and it isn't the one this opened, as it was: written to since,
    /// or replaced. Write sharing lets another app overwrite the file under the open document, which goes on
    /// drawing the old pages; reopening reads the new file. False when nothing is there (deleted, or renamed by
    /// another app unseen): this handle still reads the file, so it is better kept.
    /// </summary>
    public bool ReplacedOnDisk(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && (info.LastWriteTimeUtc, info.Length) != _stamp;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/>. Throws when the file can't be read or parsed, or is encrypted in a way
    /// Windows.Data.Pdf can't open (AES-256, or a user password).
    /// </summary>
    public static async Task<PdfFile> OpenAsync(string path)
    {
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Not FileStream.AsRandomAccessStream(): the document reads in many small pieces (3,000 to open a
        // 1000-page file), and each costs ~100 µs through that adapter.
        var stream = ClosableStream.Open(file);
        try
        {
            var stamp = (File.GetLastWriteTimeUtc(file), RandomAccess.GetLength(file));
            return new PdfFile(stream, stamp, await PdfDocument.LoadFromStreamAsync(stream.Stream));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Closes the file. Pages taken from <see cref="Document"/> must be disposed first.</summary>
    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// Renders PDF pages on the GPU with Windows.Data.Pdf's Direct2D renderer (<c>IPdfRendererNative</c>), straight into
/// a Win2D render target on the canvas's own device: no encode, no CPU copy, no extra DLL. Any thread.
/// </summary>
/// <remarks>
/// The renderer neither culls to the source rectangle nor caches decoded images between calls (spike,
/// docs/work/279-pdf-support/study.md §6): a call costs about the same for a tile as for the whole
/// page, so callers render whole views, off the W2D thread.
/// </remarks>
internal static unsafe partial class NativePdfBridge
{
    // Interface IDs, checked against windows.data.pdf.interop.h, Microsoft.Graphics.Canvas.native.h (Win2D 1.4)
    // and the Windows SDK headers.
    /// <summary>Gets the DXGI device behind a WinRT Direct3D device (Win2D's CanvasDevice).</summary>
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    /// <summary>The DXGI device <c>PdfCreateRenderer</c> takes.</summary>
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    /// <summary>Gets the Direct2D object behind a Win2D object (here, a drawing session's device context).</summary>
    private static readonly Guid IID_ICanvasResourceWrapperNative = new("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A");
    /// <summary>The device context <c>RenderPageToDeviceContext</c> draws into.</summary>
    private static readonly Guid IID_ID2D1DeviceContext = new("e8f7fe7a-191c-466d-ad95-975678bda998");

    /// <summary>Mirror of <c>PDF_RENDER_PARAMS</c> (windows.data.pdf.interop.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdfRenderParams
    {
        public float Left, Top, Right, Bottom;   // D2D_RECT_F SourceRect, page DIPs
        public uint DestinationWidth, DestinationHeight;
        public float R, G, B, A;                  // D2D_COLOR_F BackgroundColor
        public byte IgnoreHighContrast;           // BOOLEAN
    }

    /// <summary>Makes an <c>IPdfRendererNative</c> on a DXGI device (an owned reference). Ships with Windows.</summary>
    [LibraryImport("Windows.Data.Pdf.dll", EntryPoint = "PdfCreateRenderer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int PdfCreateRenderer(nint dxgiDevice, out nint renderer);

    /// <summary>
    /// One global lock around every render: IPdfRendererNative's thread safety is undocumented. A preview render of
    /// a scanned page (~0.4 s) can delay the page on screen; per-thread renderers if that shows up.
    /// </summary>
    private static readonly Lock Gate = new();
    /// <summary>The device <see cref="_renderer"/> was made for. Under <see cref="Gate"/>.</summary>
    private static CanvasDevice? _device;
    /// <summary>The renderer (IPdfRendererNative*, owned); remade when the canvas device changes. Under <see cref="Gate"/>.</summary>
    private static nint _renderer;

    /// <summary>
    /// Renders <paramref name="source"/> (page DIPs; the page's size after its /Rotate) of <paramref name="page"/>
    /// on white into a new <paramref name="width"/>×<paramref name="height"/> render target at 96 DPI, owned by the
    /// caller. Waits for the GPU, so the bitmap is complete when returned. Throws on failure.
    /// </summary>
    public static CanvasRenderTarget Render(CanvasDevice device, PdfPage page, Rect source, int width, int height)
    {
        // A last guard only: callers keep the size within the GPU limit and the page's shape (PdfReader.RenderWhole).
        width = Math.Clamp(width, 1, device.MaximumBitmapSizeInPixels);
        height = Math.Clamp(height, 1, device.MaximumBitmapSizeInPixels);
        var rt = new CanvasRenderTarget(device, width, height, 96f);
        try
        {
            lock (Gate)
            {
                var renderer = RendererFor(device);
                using (var ds = rt.CreateDrawingSession())
                {
                    // A new render target's contents are undefined. The renderer's BackgroundColor (white below) should
                    // fill the destination too; the spike cleared as well, and this clear costs nothing worth saving.
                    ds.Clear(Color.FromArgb(255, 255, 255, 255));
                    var ctx = NativeResource(ds, IID_ID2D1DeviceContext);
                    var p = new PdfRenderParams
                    {
                        Left = (float)source.Left, Top = (float)source.Top,
                        Right = (float)source.Right, Bottom = (float)source.Bottom,
                        DestinationWidth = (uint)width, DestinationHeight = (uint)height,
                        R = 1, G = 1, B = 1, A = 1, IgnoreHighContrast = 1
                    };
                    // IPdfRendererNative::RenderPageToDeviceContext(IUnknown* page, ID2D1DeviceContext*, PDF_RENDER_PARAMS*):
                    // slot 4, after IUnknown's three and RenderPageToSurface. Must run between BeginDraw and EndDraw,
                    // which the drawing session provides.
                    var hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, PdfRenderParams*, int>)VTable(renderer)[4])(
                        renderer, Ptr(page), ctx, &p);
                    Marshal.Release(ctx);
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            rt.GetPixelBytes(0, 0, 1, 1); // wait for the GPU: the caller hands the bitmap to another thread
            return rt;
        }
        catch
        {
            rt.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Under <see cref="Gate"/>. The renderer for <paramref name="device"/> (not AddRef'd), made on its DXGI device
    /// the first time, and again after the canvas device changes (device loss).
    /// </summary>
    private static nint RendererFor(CanvasDevice device)
    {
        if (ReferenceEquals(device, _device)) return _renderer;
        var access = QueryInterface(Ptr(device), IID_IDirect3DDxgiInterfaceAccess);
        nint dxgi;
        try
        {
            var iid = IID_IDXGIDevice;
            // IDirect3DDxgiInterfaceAccess::GetInterface(REFIID, void**): slot 3.
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)VTable(access)[3])(access, &iid, &dxgi));
        }
        finally
        {
            Marshal.Release(access);
        }
        try
        {
            Marshal.ThrowExceptionForHR(PdfCreateRenderer(dxgi, out var renderer));
            if (_renderer != 0) Marshal.Release(_renderer);
            (_device, _renderer) = (device, renderer);
            return renderer;
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    /// <summary>
    /// The Direct2D interface <paramref name="iid"/> behind a Win2D object, as an owned reference the caller
    /// releases: ICanvasResourceWrapperNative::GetNativeResource (vtable slot 3).
    /// </summary>
    private static nint NativeResource(object win2dObject, Guid iid)
    {
        var wrapper = QueryInterface(Ptr(win2dObject), IID_ICanvasResourceWrapperNative);
        try
        {
            nint resource;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, float, Guid*, nint*, int>)VTable(wrapper)[3])(
                wrapper, 0, 0f, &iid, &resource));
            return resource;
        }
        finally
        {
            Marshal.Release(wrapper);
        }
    }

    /// <summary>A CsWinRT-projected object's COM pointer (not AddRef'd; valid while the object is alive).</summary>
    private static nint Ptr(object winrtObject) => ((IWinRTObject)winrtObject).NativeObject.ThisPtr;

    /// <summary>A COM object's vtable, for calling a method by its slot number.</summary>
    private static nint* VTable(nint com) => *(nint**)com;

    /// <summary>QueryInterface that throws on failure; the result is an owned reference the caller releases.</summary>
    private static nint QueryInterface(nint unknown, Guid iid)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var p));
        return p;
    }
}
