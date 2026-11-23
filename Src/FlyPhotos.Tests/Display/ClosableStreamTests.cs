using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using FlyPhotos.Display.Animators;
using FlyPhotos.Infra.Interop;
using FlyPhotos.Infra.Utils;
using FlyPhotos.Tests.Display.Harness;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The decoders' native-speed stream (<see cref="ClosableStream"/>) and its users: the GIF animator, multi-page
/// decodes, the APNG animator's slicing parser, and PDFs, which must close their file at Dispose.
/// </summary>
[TestClass]
public sealed class ClosableStreamTests
{
    private static readonly CanvasRenderTarget Ctrl = new(TestGpu.Device, 1, 1, 96);
    private static readonly Color[] Greys = [Color.FromArgb(255, 0, 0, 0), Color.FromArgb(255, 90, 90, 90), Color.FromArgb(255, 180, 180, 180)];
    private string _folder = null!;

    [TestInitialize]
    public void Setup() =>
        _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FlyClosable_" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // An image file of `frames` solid-grey frames (Greys[i]) from a WIC encoder.
    private static async Task<byte[]> Encode(Guid encoderId, int frames, int width = 40, int height = 30)
    {
        using var ras = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, ras);
        for (int f = 0; f < frames; f++)
        {
            if (f > 0) await encoder.GoToNextFrameAsync();
            var px = new byte[width * height * 4];
            for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 1], px[i + 2], px[i + 3]) = (Greys[f].B, Greys[f].G, Greys[f].R, 255);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, px);
        }
        await encoder.FlushAsync();
        var bytes = new byte[ras.Size];
        ras.Seek(0);
        await ras.ReadAsync(bytes.AsBuffer(), (uint)ras.Size, InputStreamOptions.None);
        return bytes;
    }

    private static Color Centre(IAnimator animator)
    {
        using var target = new CanvasRenderTarget(TestGpu.Device, animator.PixelWidth, animator.PixelHeight, 96);
        using (var ds = target.CreateDrawingSession()) ds.DrawImage(animator.Surface);
        return target.GetPixelColors((int)animator.PixelWidth / 2, (int)animator.PixelHeight / 2, 1, 1)[0];
    }

    [TestMethod]
    public async Task An_avif_animates_from_its_pinned_bytes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Heif", "animated_rgb_once.avif");
        using var animator = await AvifAnimator.CreateAsync(await File.ReadAllBytesAsync(path), Ctrl);
        await animator.UpdateAsync(TimeSpan.Zero);
        Assert.AreEqual(255, Centre(animator).R, "the first frame is red");
    }

    [TestMethod]
    public async Task A_gif_animates_over_its_bytes()
    {
        var gif = await Encode(BitmapEncoder.GifEncoderId, 3);
        using var animator = await GifAnimator.CreateAsync(gif, Ctrl);
        for (int frame = 0; frame < 3; frame++)
        {
            await animator.UpdateAsync(TimeSpan.FromMilliseconds(frame * 100 + 50));
            Assert.AreEqual(Greys[frame].R, Centre(animator).R, $"frame {frame}");
        }
    }

    [TestMethod]
    public async Task Every_page_of_a_tiff_decodes_and_reads_fail_once_closed()
    {
        var tiff = await Encode(BitmapEncoder.TiffEncoderId, 3, 400, 300);
        using (var opened = ClosableStream.Open(tiff))
        {
            var decoder = await BitmapDecoder.CreateAsync(opened.Stream);
            Assert.AreEqual(3u, decoder.FrameCount);
            var frame = await decoder.GetFrameAsync(2);
            var pixels = (await frame.GetPixelDataAsync()).DetachPixelData();
            Assert.AreEqual(Greys[2].R, pixels[0]);
        }

        // A decoder still holding the stream after Dispose: its next read fails instead of reading dropped bytes.
        var held = ClosableStream.Open(tiff);
        var late = await BitmapDecoder.CreateAsync(held.Stream);
        held.Dispose();
        try
        {
            var frame = await late.GetFrameAsync(2);
            await frame.GetPixelDataAsync();
            Assert.Fail("a read after Dispose must fail");
        }
        catch (Exception ex) when (ex is not AssertFailedException) { }
    }

    [TestMethod]
    public async Task Native_memory_decodes_in_place()
    {
        var png = await EncodeSingle(Greys[2]);
        nint native = System.Runtime.InteropServices.Marshal.AllocHGlobal(png.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(png, 0, native, png.Length);
            CanvasBitmap bitmap;
            using (var stream = Open(native, png.Length)) bitmap = await CanvasBitmap.LoadAsync(Ctrl, stream.Stream);
            using (bitmap) Assert.AreEqual(Greys[2].R, bitmap.GetPixelColors(0, 0, 1, 1)[0].R);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(native);
        }

        static unsafe ClosableStream.Opened Open(nint at, int length) => ClosableStream.Open((byte*)at, length);
    }

    // An APNG of two full-canvas frames, the first also the default image, from WIC-encoded PNGs.
    private static async Task<byte[]> Apng()
    {
        var frames = new[] { await EncodeSingle(Greys[0]), await EncodeSingle(Greys[1]) };
        var output = new MemoryStream();
        output.Write(frames[0], 0, 8); // signature
        var (ihdr, idat0) = (Chunks(frames[0]).First(c => c.Type == "IHDR"), Chunks(frames[0]).Where(c => c.Type == "IDAT"));
        Write(output, "IHDR", ihdr.Data);
        Write(output, "acTL", [.. U32(2), .. U32(0)]);
        Write(output, "fcTL", Fctl(0));
        foreach (var c in idat0) Write(output, "IDAT", c.Data);
        Write(output, "fcTL", Fctl(1));
        uint seq = 2;
        foreach (var c in Chunks(frames[1]).Where(c => c.Type == "IDAT")) Write(output, "fdAT", [.. U32(seq++), .. c.Data]);
        Write(output, "IEND", []);
        return output.ToArray();

        static byte[] Fctl(uint seq) => [.. U32(seq), .. U32(40), .. U32(30), .. U32(0), .. U32(0), 0, 10, 0, 100, 0, 0];
        static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
        static void Write(Stream s, string type, byte[] data)
        {
            var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            s.Write(U32((uint)data.Length));
            s.Write(typed);
            s.Write(U32(Crc32.Compute(typed)));
        }
        static IEnumerable<(string Type, byte[] Data)> Chunks(byte[] png)
        {
            for (int pos = 8; pos + 8 <= png.Length;)
            {
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
                yield return (Encoding.ASCII.GetString(png, pos + 4, 4), png[(pos + 8)..(pos + 8 + len)]);
                pos += 12 + len;
            }
        }
    }

    private static async Task<byte[]> EncodeSingle(Color c)
    {
        using var ras = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
        var px = new byte[40 * 30 * 4];
        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 1], px[i + 2], px[i + 3]) = (c.B, c.G, c.R, 255);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 40, 30, 96, 96, px);
        await encoder.FlushAsync();
        var bytes = new byte[ras.Size];
        ras.Seek(0);
        await ras.ReadAsync(bytes.AsBuffer(), (uint)ras.Size, InputStreamOptions.None);
        return bytes;
    }

    [TestMethod]
    public async Task An_apng_animates_from_slices_of_its_bytes()
    {
        using var animator = await PngAnimator.CreateAsync(await Apng(), Ctrl);
        Assert.AreEqual(40u, animator.PixelWidth);
        await animator.UpdateAsync(TimeSpan.FromMilliseconds(50));
        Assert.AreEqual(Greys[0].R, Centre(animator).R, "frame 0 (the default image)");
        await animator.UpdateAsync(TimeSpan.FromMilliseconds(150));
        Assert.AreEqual(Greys[1].R, Centre(animator).R, "frame 1 (fdAT)");
    }

    // A PDF of `pages` empty A4 pages, with a correct xref table.
    private string Pdf(string name, int pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{i + 3} 0 R"))}] /Count {pages} >>"
        };
        objects.AddRange(Enumerable.Range(0, pages).Select(_ => "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>"));
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }

    private static bool NoOtherHandle(string path)
    {
        try
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [TestMethod]
    public async Task A_pdf_can_be_renamed_and_deleted_while_open()
    {
        var path = Pdf("a.pdf", 5);
        using var pdf = await PdfFile.OpenAsync(path);
        Assert.AreEqual(5u, pdf.Document.PageCount);
        Assert.IsFalse(pdf.ReplacedOnDisk(path));

        var renamed = Path.Combine(_folder, "b.pdf");
        File.Move(path, renamed);
        File.Delete(renamed);
        Assert.IsFalse(File.Exists(renamed));
        using var page = pdf.Document.GetPage(4);
        Assert.AreEqual(595 * 96 / 72.0, page.Size.Width, 1, "read after the delete; DIPs, not points");
    }

    [TestMethod]
    public async Task A_pdf_closes_its_file_at_dispose_though_the_document_lives_on()
    {
        var path = Pdf("a.pdf", 2);
        var pdf = await PdfFile.OpenAsync(path);
        var document = pdf.Document;
        Assert.IsFalse(NoOtherHandle(path), "held while open");
        pdf.Dispose();
        Assert.IsTrue(NoOtherHandle(path), "closed at Dispose, not at a later GC");
        GC.KeepAlive(document);
    }

    [TestMethod]
    public async Task A_pdf_overwritten_in_place_is_seen_as_replaced()
    {
        var path = Pdf("a.pdf", 2);
        using var pdf = await PdfFile.OpenAsync(path);
        File.WriteAllText(path, File.ReadAllText(path) + "\n% changed\n", Encoding.ASCII);
        Assert.IsTrue(pdf.ReplacedOnDisk(path));
    }
}
