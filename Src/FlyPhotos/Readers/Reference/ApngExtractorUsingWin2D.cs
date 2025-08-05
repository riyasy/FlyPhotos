//// Required using statements for Win2D
//using Microsoft.Graphics.Canvas;
//using Microsoft.Graphics.Canvas.UI.Xaml;
//using Windows.Foundation;
//using Windows.Storage.Streams;
//using Windows.UI;

//// Keep these from your original code
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;
//using Microsoft.UI;
//using Buffer = System.Buffer;

///// <summary>
///// A class to read an APNG (Animated PNG) file and extract its frames using Win2D.
///// This implementation correctly composites frames to produce a sequence of CanvasBitmaps.
///// It is based on the specification from https://wiki.mozilla.org/APNG_Specification.
///// REQUIRES the 'Microsoft.Graphics.Win2D' NuGet package.
///// </summary>
//public static class ApngExtractorUsingWin2D
//{
//    // These constants and nested classes are identical to your original code.
//    // I am including them here for completeness.
//    #region Constants and Nested Classes

//    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
//    private const string AnimationControlChunkType = "acTL";
//    private const string FrameControlChunkType = "fcTL";
//    private const string FrameDataChunkType = "fdAT";
//    private const byte APNG_DISPOSE_OP_NONE = 0;
//    private const byte APNG_DISPOSE_OP_BACKGROUND = 1;
//    private const byte APNG_DISPOSE_OP_PREVIOUS = 2;
//    private const byte APNG_BLEND_OP_SOURCE = 0;
//    private const byte APNG_BLEND_OP_OVER = 1;

//    private class PngChunk
//    {
//        public string Type { get; set; }
//        public byte[] Data { get; set; }
//    }

//    private class FrameControl
//    {
//        public uint SequenceNumber { get; set; }
//        public uint Width { get; set; }
//        public uint Height { get; set; }
//        public uint XOffset { get; set; }
//        public uint YOffset { get; set; }
//        public ushort DelayNum { get; set; }
//        public ushort DelayDen { get; set; }
//        public byte DisposeOp { get; set; }
//        public byte BlendOp { get; set; }
//        public List<PngChunk> FrameDataChunks { get; } = [];
//    }

//    #endregion

//    #region Public API

//    /// <summary>
//    /// Extracts APNG frames and saves them to disk as individual PNG files.
//    /// </summary>
//    /// <param name="resourceCreator">A Win2D resource creator, like a CanvasControl.</param>
//    /// <param name="filePath">The path to the APNG file.</param>
//    /// <returns>A list of file paths for the saved frames.</returns>
//    public static async Task<List<string>> ExtractAndSaveFramesAsync(ICanvasResourceCreatorWithDpi resourceCreator, string filePath)
//    {
//        if (!File.Exists(filePath)) throw new FileNotFoundException("The specified APNG file was not found.", filePath);

//        var canvasBitmaps = await ExtractFramesAsync(resourceCreator, filePath);

//        var sourceDirectory = Path.GetDirectoryName(filePath);
//        var baseFileName = Path.GetFileNameWithoutExtension(filePath);
//        var outputDirectory = Path.Combine(sourceDirectory, $"{baseFileName}_frames");
//        Directory.CreateDirectory(outputDirectory);

//        var savedFilePaths = new List<string>();
//        for (var i = 0; i < canvasBitmaps.Count; i++)
//        {
//            using var frame = canvasBitmaps[i];
//            var outputFileName = $"frame_{i:D3}.png";
//            var outputPath = Path.Combine(outputDirectory, outputFileName);

//            // Save the CanvasBitmap to a file
//            await frame.SaveAsync(outputPath, CanvasBitmapFileFormat.Png);
//            savedFilePaths.Add(outputPath);
//        }

//        Console.WriteLine($"Successfully extracted and saved {savedFilePaths.Count} frames to '{outputDirectory}'");
//        // The caller receives the paths, and the bitmaps are disposed correctly.
//        return savedFilePaths;
//    }

//    /// <summary>
//    /// Extracts all frames from an APNG file into a list of Win2D CanvasBitmaps.
//    /// The caller is responsible for disposing the bitmaps in the returned list.
//    /// </summary>
//    /// <param name="resourceCreator">A Win2D resource creator, e.g., a CanvasControl or CanvasDevice. Must not be null.</param>
//    /// <param name="filePath">Path to the APNG file.</param>
//    /// <returns>A Task that resolves to a List of CanvasBitmap objects.</returns>
//    public static async Task<List<CanvasBitmap>> ExtractFramesAsync(ICanvasResourceCreatorWithDpi resourceCreator, string filePath)
//    {
//        if (!File.Exists(filePath)) throw new FileNotFoundException("The specified APNG file was not found.", filePath);
//        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
//        return await ExtractFramesAsync(resourceCreator, fileStream);
//    }

//    /// <summary>
//    /// Extracts all frames from an APNG stream into a list of Win2D CanvasBitmaps.
//    /// The caller is responsible for disposing the bitmaps in the returned list.
//    /// </summary>
//    /// <param name="resourceCreator">A Win2D resource creator, e.g., a CanvasControl or CanvasDevice. Must not be null.</param>
//    /// <param name="apngStream">Stream containing the APNG data.</param>
//    /// <returns>A Task that resolves to a List of CanvasBitmap objects.</returns>
//    public static async Task<List<CanvasBitmap>> ExtractFramesAsync(ICanvasResourceCreatorWithDpi resourceCreator, Stream apngStream)
//    {
//        if (resourceCreator == null) throw new ArgumentNullException(nameof(resourceCreator));

//        // The entire chunk parsing logic is IDENTICAL to the original.
//        // It's pure byte manipulation and does not depend on the graphics library.
//        using var reader = new BinaryReader(apngStream);
//        if (!reader.ReadBytes(PngSignature.Length).SequenceEqual(PngSignature))
//            throw new ArgumentException("The specified file is not a valid PNG file.");

//        var chunks = ReadAllChunks(reader);
//        var globalChunks = new List<PngChunk>();
//        var defaultImageIdatChunks = new List<PngChunk>();
//        var frameControls = new List<FrameControl>();
//        PngChunk ihdrChunk = null;
//        var isApng = false;

//        foreach (var chunk in chunks)
//        {
//            switch (chunk.Type)
//            {
//                case "IHDR":
//                    ihdrChunk = chunk;
//                    globalChunks.Add(chunk);
//                    break;
//                case AnimationControlChunkType:
//                    isApng = true;
//                    break;
//                case FrameControlChunkType:
//                    frameControls.Add(ParseFrameControl(chunk));
//                    break;
//                case FrameDataChunkType:
//                    if (frameControls.Any()) frameControls.Last().FrameDataChunks.Add(ConvertFdatToIdat(chunk));
//                    break;
//                case "IDAT":
//                    // This includes the recommended fix from the previous review.
//                    if (!frameControls.Any()) defaultImageIdatChunks.Add(chunk);
//                    else frameControls.Last().FrameDataChunks.Add(chunk);
//                    break;
//                default:
//                    if (!frameControls.Any() && chunk.Type != "IEND") globalChunks.Add(chunk);
//                    break;
//            }
//        }

//        if (ihdrChunk == null) throw new InvalidDataException("IHDR chunk not found.");
//        if (!isApng)
//        {
//            apngStream.Position = 0;
//            // For a non-animated PNG, just load it directly.
//            var simpleBitmap = await CanvasBitmap.LoadAsync(resourceCreator, apngStream.AsRandomAccessStream());
//            return [simpleBitmap];
//        }

//        var orderedFrames = frameControls.OrderBy(fc => fc.SequenceNumber).ToList();
//        return await CompositeFramesAsync(resourceCreator, ihdrChunk, globalChunks, defaultImageIdatChunks, orderedFrames);
//    }

//    #endregion

//    #region Private Win2D Implementation

//    /// <summary>
//    /// The definitive, spec-compliant compositing engine, implemented with Win2D.
//    /// </summary>
//    private static async Task<List<CanvasBitmap>> CompositeFramesAsync(
//        ICanvasResourceCreatorWithDpi resourceCreator,
//        PngChunk ihdrChunk,
//        List<PngChunk> globalChunks,
//        List<PngChunk> defaultImageIdatChunks,
//        List<FrameControl> frameControls)
//    {
//        var history = new List<CanvasBitmap>();
//        var canvasWidth = ReadUInt32BigEndian(ihdrChunk.Data, 0);
//        var canvasHeight = ReadUInt32BigEndian(ihdrChunk.Data, 4);

//        // In Win2D, a drawable bitmap is a CanvasRenderTarget.
//        using var compositedCanvas = new CanvasRenderTarget(resourceCreator, canvasWidth, canvasHeight);
//        CanvasRenderTarget previousCanvas = null; // For APNG_DISPOSE_OP_PREVIOUS

//        // The default image handling logic remains the same conceptually.
//        var defaultFrameControl = frameControls.FirstOrDefault(fc => fc.SequenceNumber == 0 && defaultImageIdatChunks.Any());
//        if (defaultFrameControl != null)
//        {
//            using var defaultFrame = await CreateImageFromChunksAsync(resourceCreator, ihdrChunk, globalChunks.Where(c => c.Type != "IHDR"), defaultImageIdatChunks);
//            var patchRect = new Rect(0, 0, canvasWidth, canvasHeight);

//            using (var ds = compositedCanvas.CreateDrawingSession())
//            {
//                if (defaultFrameControl.BlendOp == APNG_BLEND_OP_SOURCE)
//                {
//                    // SOURCE blend: Clear the area, then overwrite pixels.
//                    ds.FillRectangle(patchRect, Colors.Transparent);
//                    ds.DrawImage(defaultFrame, (float)patchRect.X, (float)patchRect.Y, defaultFrame.Bounds, 1.0f, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
//                }
//                else // APNG_BLEND_OP_OVER
//                {
//                    ds.DrawImage(defaultFrame);
//                }
//            }

//            history.Add(CloneRenderTarget(resourceCreator, compositedCanvas));

//            // Apply disposal op
//            if (defaultFrameControl.DisposeOp == APNG_DISPOSE_OP_BACKGROUND || defaultFrameControl.DisposeOp == APNG_DISPOSE_OP_PREVIOUS)
//            {
//                using var ds = compositedCanvas.CreateDrawingSession();
//                ds.FillRectangle(patchRect, Colors.Transparent);
//            }
//        }

//        // Process each subsequent frame.
//        foreach (var fc in frameControls.Where(fc => !defaultImageIdatChunks.Any() || fc.SequenceNumber > 0))
//        {
//            if (fc.XOffset + fc.Width > canvasWidth || fc.YOffset + fc.Height > canvasHeight)
//                throw new InvalidDataException($"Frame {fc.SequenceNumber} region exceeds canvas dimensions.");

//            // Clone the current canvas state to support APNG_DISPOSE_OP_PREVIOUS
//            previousCanvas?.Dispose();
//            previousCanvas = CloneRenderTarget(resourceCreator, compositedCanvas);

//            // Create the frame's patch image from its chunks.
//            using var patch = await CreateImageFromChunksAsync(resourceCreator, ihdrChunk, globalChunks.Where(c => c.Type != "IHDR"), fc.FrameDataChunks, fc.Width, fc.Height);
//            var patchRect = new Rect(fc.XOffset, fc.YOffset, fc.Width, fc.Height);

//            // Draw onto the main canvas.
//            using (var ds = compositedCanvas.CreateDrawingSession())
//            {
//                if (fc.BlendOp == APNG_BLEND_OP_SOURCE)
//                {
//                    // SOURCE blend: clear the area, then overwrite with a 'Copy' composite mode.
//                    ds.FillRectangle(patchRect, Colors.Transparent);
//                    ds.DrawImage(patch, (float)patchRect.X, (float)patchRect.Y, patch.Bounds, 1.0f, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
//                }
//                else // APNG_BLEND_OP_OVER
//                {
//                    // OVER blend: standard alpha blending.
//                    ds.DrawImage(patch, (float)patchRect.X, (float)patchRect.Y);
//                }
//            }

//            // Save the rendered frame to history before applying disposal.
//            history.Add(CloneRenderTarget(resourceCreator, compositedCanvas));

//            // Apply disposal operation.
//            using (var ds = compositedCanvas.CreateDrawingSession())
//            {
//                switch (fc.DisposeOp)
//                {
//                    case APNG_DISPOSE_OP_BACKGROUND:
//                        // Clear the patch region to fully transparent.
//                        ds.FillRectangle(patchRect, Colors.Transparent);
//                        break;
//                    case APNG_DISPOSE_OP_PREVIOUS:
//                        // Revert by blitting the previous canvas state back over the patch region.
//                        ds.DrawImage(previousCanvas, (float)patchRect.X, (float)patchRect.Y, patchRect, 1.0f, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
//                        break;
//                        // case APNG_DISPOSE_OP_NONE: do nothing.
//                }
//            }
//        }

//        previousCanvas?.Dispose();
//        return history;
//    }

//    /// <summary>
//    /// Creates a decodable in-memory PNG stream and loads it into a Win2D CanvasBitmap.
//    /// </summary>
//    private static async Task<CanvasBitmap> CreateImageFromChunksAsync(ICanvasResourceCreator resourceCreator, PngChunk ihdrChunk, IEnumerable<PngChunk> otherChunks,
//        IEnumerable<PngChunk> dataChunks, uint? frameWidth = null, uint? frameHeight = null)
//    {
//        // This helper function remains mostly the same, but the return type is now Task<CanvasBitmap>.
//        using var ms = new MemoryStream();
//        using var writer = new BinaryWriter(ms);
//        writer.Write(PngSignature);
//        var ihdrData = (byte[])ihdrChunk.Data.Clone();
//        if (frameWidth.HasValue && frameHeight.HasValue)
//        {
//            Array.Copy(GetBytesBigEndian(frameWidth.Value), 0, ihdrData, 0, 4);
//            Array.Copy(GetBytesBigEndian(frameHeight.Value), 0, ihdrData, 4, 4);
//        }

//        WriteChunk(writer, "IHDR", ihdrData);
//        foreach (var chunk in otherChunks) WriteChunk(writer, chunk.Type, chunk.Data);
//        foreach (var chunk in dataChunks) WriteChunk(writer, chunk.Type, chunk.Data);
//        WriteChunk(writer, "IEND", []);
//        ms.Position = 0;

//        // Load the in-memory PNG stream into a CanvasBitmap.
//        return await CanvasBitmap.LoadAsync(resourceCreator, ms.AsRandomAccessStream());
//    }

//    /// <summary>
//    /// Clones a CanvasRenderTarget by creating a new one and drawing the source onto it.
//    /// This is necessary to create snapshots for the history and for DISPOSE_OP_PREVIOUS.
//    /// </summary>
//    private static CanvasRenderTarget CloneRenderTarget(ICanvasResourceCreator resourceCreator, CanvasRenderTarget source)
//    {
//        var clone = new CanvasRenderTarget(resourceCreator, source.SizeInPixels.Width, source.SizeInPixels.Height, source.Dpi);
//        using (var ds = clone.CreateDrawingSession())
//        {
//            ds.DrawImage(source);
//        }
//        return clone;
//    }

//    #endregion

//    #region PNG/Binary Helpers (Unchanged)

//    // All of these helper methods are identical to the original and do not need to be modified.
//    // ReadAllChunks, ParseFrameControl, ConvertFdatToIdat, WriteChunk,
//    // ReadUInt32BigEndian, ReadUInt16BigEndian, WriteUInt32BigEndian, GetBytesBigEndian, and the Crc32 class.

//    private static List<PngChunk> ReadAllChunks(BinaryReader r)
//    {
//        var c = new List<PngChunk>();
//        while (r.BaseStream.Position < r.BaseStream.Length)
//            try
//            {
//                var l = ReadUInt32BigEndian(r);
//                var t = Encoding.ASCII.GetString(r.ReadBytes(4));
//                var d = r.ReadBytes((int)l);
//                ReadUInt32BigEndian(r);
//                c.Add(new PngChunk { Type = t, Data = d });
//                if (t == "IEND") break;
//            }
//            catch (EndOfStreamException)
//            {
//                break;
//            }

//        return c;
//    }

//    private static FrameControl ParseFrameControl(PngChunk fc)
//    {
//        using var r = new BinaryReader(new MemoryStream(fc.Data));
//        return new FrameControl
//        {
//            SequenceNumber = ReadUInt32BigEndian(r),
//            Width = ReadUInt32BigEndian(r),
//            Height = ReadUInt32BigEndian(r),
//            XOffset = ReadUInt32BigEndian(r),
//            YOffset = ReadUInt32BigEndian(r),
//            DelayNum = ReadUInt16BigEndian(r),
//            DelayDen = ReadUInt16BigEndian(r),
//            DisposeOp = r.ReadByte(),
//            BlendOp = r.ReadByte()
//        };
//    }

//    private static PngChunk ConvertFdatToIdat(PngChunk f)
//    {
//        var d = new byte[f.Data.Length - 4];
//        Array.Copy(f.Data, 4, d, 0, d.Length);
//        return new PngChunk { Type = "IDAT", Data = d };
//    }

//    private static void WriteChunk(BinaryWriter w, string t, byte[] d)
//    {
//        WriteUInt32BigEndian(w, (uint)d.Length);
//        var b = Encoding.ASCII.GetBytes(t);
//        w.Write(b);
//        if (d.Length > 0) w.Write(d);
//        var c = new byte[b.Length + d.Length];
//        Buffer.BlockCopy(b, 0, c, 0, b.Length);
//        if (d.Length > 0) Buffer.BlockCopy(d, 0, c, b.Length, d.Length);
//        WriteUInt32BigEndian(w, Crc32.Compute(c));
//    }

//    private static uint ReadUInt32BigEndian(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
//    private static uint ReadUInt32BigEndian(BinaryReader r) => (uint)((r.ReadByte() << 24) | (r.ReadByte() << 16) | (r.ReadByte() << 8) | r.ReadByte());
//    private static ushort ReadUInt16BigEndian(BinaryReader r) => (ushort)((r.ReadByte() << 8) | r.ReadByte());
//    private static void WriteUInt32BigEndian(BinaryWriter w, uint v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
//    private static byte[] GetBytesBigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

//    #endregion
//}
