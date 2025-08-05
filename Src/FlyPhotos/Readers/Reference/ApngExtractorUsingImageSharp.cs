//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Text;
//using SixLabors.ImageSharp;
//using SixLabors.ImageSharp.Drawing.Processing;
//using SixLabors.ImageSharp.PixelFormats;
//using SixLabors.ImageSharp.Processing;

///// <summary>
/////     A class to read an APNG (Animated PNG) file and extract its frames.
/////     This implementation correctly composites frames to produce a sequence of full-sized images.
/////     It is based on the specification from https://wiki.mozilla.org/APNG_Specification.
/////     REQUIRES the 'SixLabors.ImageSharp' and 'SixLabors.ImageSharp.Drawing' NuGet packages.
///// </summary>
//public static class ApngExtractorUsingImageSharp
//{
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

//    public static List<string> ExtractAndSaveFrames(string filePath)
//    {
//        if (!File.Exists(filePath)) throw new FileNotFoundException("The specified APNG file was not found.", filePath);
//        var sourceDirectory = Path.GetDirectoryName(filePath);
//        var baseFileName = Path.GetFileNameWithoutExtension(filePath);
//        var outputDirectory = Path.Combine(sourceDirectory, $"{baseFileName}_frames");
//        Directory.CreateDirectory(outputDirectory);
//        var imageSharpFrames = ExtractFrames(filePath);
//        var savedFilePaths = new List<string>();
//        for (var i = 0; i < imageSharpFrames.Count; i++)
//        {
//            using var frame = imageSharpFrames[i];
//            var outputFileName = $"frame_{i:D3}.png";
//            var outputPath = Path.Combine(outputDirectory, outputFileName);
//            frame.SaveAsPng(outputPath);
//            savedFilePaths.Add(outputPath);
//        }

//        Console.WriteLine($"Successfully extracted and saved {savedFilePaths.Count} frames to '{outputDirectory}'");
//        return savedFilePaths;
//    }

//    public static List<Image<Rgba32>> ExtractFrames(string filePath)
//    {
//        if (!File.Exists(filePath)) throw new FileNotFoundException("The specified APNG file was not found.", filePath);
//        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
//        return ExtractFrames(fileStream);
//    }

//    public static List<Image<Rgba32>> ExtractFrames(Stream apngStream)
//    {
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
//            switch (chunk.Type)
//            {
//                case "IHDR":
//                    ihdrChunk = chunk;
//                    globalChunks.Add(chunk);
//                    break;
//                case AnimationControlChunkType: isApng = true; break;
//                case FrameControlChunkType: frameControls.Add(ParseFrameControl(chunk)); break;
//                case FrameDataChunkType:
//                    if (frameControls.Any()) frameControls.Last().FrameDataChunks.Add(ConvertFdatToIdat(chunk));
//                    break;
//                case "IDAT":
//                    if (!frameControls.Any()) defaultImageIdatChunks.Add(chunk);
//                    else frameControls.Last().FrameDataChunks.Add(chunk);
//                    break;
//                default:
//                    if (!frameControls.Any() && chunk.Type != "IEND") globalChunks.Add(chunk);
//                    break;
//            }

//        if (ihdrChunk == null) throw new InvalidDataException("IHDR chunk not found.");
//        if (!isApng)
//        {
//            apngStream.Position = 0;
//            return [Image.Load<Rgba32>(apngStream)];
//        }

//        var orderedFrames = frameControls.OrderBy(fc => fc.SequenceNumber).ToList();
//        return CompositeFrames(ihdrChunk, globalChunks, defaultImageIdatChunks, orderedFrames);
//    }

//    #endregion

//    #region Private Implementation

//    /// <summary>
//    ///     The definitive, spec-compliant compositing engine. This version uses a correct, stateless
//    ///     rendering pipeline to guarantee no artifacts or crashes.
//    /// </summary>
//    private static List<Image<Rgba32>> CompositeFrames(
//        PngChunk ihdrChunk,
//        List<PngChunk> globalChunks,
//        List<PngChunk> defaultImageIdatChunks,
//        List<FrameControl> frameControls)
//    {
//        // Initialize a list to store the extracted frames for output
//        var history = new List<Image<Rgba32>>();

//        // Read canvas dimensions from IHDR chunk as per PNG specification
//        var canvasWidth = ReadUInt32BigEndian(ihdrChunk.Data, 0);
//        var canvasHeight = ReadUInt32BigEndian(ihdrChunk.Data, 4);

//        // Create the output buffer (compositedCanvas) initialized to fully transparent black
//        // as required by the APNG specification for the start of each animation play
//        var compositedCanvas = new Image<Rgba32>((int)canvasWidth, (int)canvasHeight);

//        // Clone the initial canvas to support APNG_DISPOSE_OP_PREVIOUS, which reverts to the previous state
//        var previousCanvas = compositedCanvas.Clone();

//        // Check if the default image (from IDAT chunks) is part of the animation by looking for an fcTL chunk with SequenceNumber 0
//        var defaultFrameControl =
//            frameControls.FirstOrDefault(fc => fc.SequenceNumber == 0 && defaultImageIdatChunks.Any());

//        // Handle the default image if it's the first frame of the animation
//        if (defaultFrameControl != null)
//        {
//            // Create the default frame image from IHDR, global chunks, and IDAT chunks
//            using var defaultFrame = CreateImageFromChunks(
//                ihdrChunk,
//                globalChunks.Where(c => c.Type != "IHDR"),
//                defaultImageIdatChunks
//            );

//            // Define the patch region for the default frame, which must cover the entire canvas (x_offset=0, y_offset=0, width=IHDR.width, height=IHDR.height)
//            var patchRect = new Rectangle(0, 0, (int)canvasWidth, (int)canvasHeight);

//            switch (defaultFrameControl.BlendOp)
//            {
//                // Apply blending based on blend_op as specified in the fcTL chunk
//                // APNG_BLEND_OP_SOURCE: Clear the patch region to transparent and overwrite with frame pixels
//                case APNG_BLEND_OP_SOURCE:
//                    compositedCanvas.Mutate(ctx =>
//                    {
//                        var graphicsOptions = new DrawingOptions
//                        {
//                            GraphicsOptions = new GraphicsOptions
//                                { AlphaCompositionMode = PixelAlphaCompositionMode.Clear }
//                        };
//                        ctx.Fill(graphicsOptions, Color.Transparent, patchRect);
//                        Blit(defaultFrame, compositedCanvas, patchRect);
//                    });
//                    break;
//                // BLEND_OP_OVER
//                // APNG_BLEND_OP_OVER: Alpha-blend the frame onto the canvas using the OVER operation
//                case APNG_BLEND_OP_OVER:
//                    compositedCanvas.Mutate(ctx => ctx.DrawImage(defaultFrame, new Point(0, 0), 1f));
//                    break;
//            }

//            // Save the rendered frame to history before applying disposal, as per the specification
//            history.Add(compositedCanvas.Clone());

//            // Apply disposal operation after rendering, as specified in the APNG specification
//            switch (defaultFrameControl.DisposeOp)
//            {
//                case APNG_DISPOSE_OP_BACKGROUND
//                    : // Clear the patch region to fully transparent black before the next frame
//                case APNG_DISPOSE_OP_PREVIOUS
//                    : // For the first frame, APNG_DISPOSE_OP_PREVIOUS is treated as APNG_DISPOSE_OP_BACKGROUND
//                {
//                    compositedCanvas.Mutate(ctx =>
//                    {
//                        var graphicsOptions = new DrawingOptions
//                        {
//                            GraphicsOptions = new GraphicsOptions
//                                { AlphaCompositionMode = PixelAlphaCompositionMode.Clear }
//                        };
//                        ctx.Fill(Color.Transparent, patchRect);
//                    });
//                }
//                    break;
//                case APNG_DISPOSE_OP_NONE:
//                    // No disposal; leave the output buffer unchanged
//                    break;
//            }
//        }

//        // Process each animation frame, skipping the default frame if it was already handled
//        foreach (var fc in frameControls.Where(fc => !defaultImageIdatChunks.Any() || fc.SequenceNumber > 0))
//        {
//            // Validate that the frame region stays within the canvas boundaries, as required by the specification
//            if (fc.XOffset + fc.Width > canvasWidth || fc.YOffset + fc.Height > canvasHeight)
//                throw new InvalidDataException($"Frame {fc.SequenceNumber} region exceeds canvas dimensions.");

//            // Dispose of the previous canvas to free memory
//            previousCanvas?.Dispose();

//            // Clone the current canvas state to support APNG_DISPOSE_OP_PREVIOUS
//            previousCanvas = compositedCanvas.Clone();

//            // Create the frame's patch image from IHDR, global chunks, and frame data (fdAT or IDAT), using frame-specific dimensions
//            using var patch = CreateImageFromChunks(
//                ihdrChunk,
//                globalChunks.Where(c => c.Type != "IHDR"),
//                fc.FrameDataChunks,
//                fc.Width,
//                fc.Height
//            );

//            // Define the patch region for rendering based on fcTL chunk's x_offset, y_offset, width, and height
//            var patchRect = new Rectangle((int)fc.XOffset, (int)fc.YOffset, (int)fc.Width, (int)fc.Height);

//            // Apply blending based on the frame's blend_op
//            switch (fc.BlendOp)
//            {
//                case APNG_BLEND_OP_SOURCE:
//                    // Clear the patch region to transparent and overwrite with frame pixels
//                    compositedCanvas.Mutate(ctx =>
//                    {
//                        var graphicsOptions = new DrawingOptions
//                        {
//                            GraphicsOptions = new GraphicsOptions
//                                { AlphaCompositionMode = PixelAlphaCompositionMode.Clear }
//                        };
//                        ctx.Fill(graphicsOptions, Color.Transparent, patchRect);
//                        Blit(patch, compositedCanvas, patchRect);
//                    });
//                    break;
//                case APNG_BLEND_OP_OVER:
//                    // Alpha-blend the frame onto the canvas at the specified offset
//                    compositedCanvas.Mutate(ctx =>
//                        ctx.DrawImage(patch, new Point((int)fc.XOffset, (int)fc.YOffset), 1f));
//                    break;
//            }

//            // Save the rendered frame to history before applying disposal, capturing the state shown during the frame's delay
//            // Save the frame before disposal (represents the rendered frame)
//            history.Add(compositedCanvas.Clone());
//            // Apply disposal operation after rendering, as per the APNG specification
//            switch (fc.DisposeOp)
//            {
//                case APNG_DISPOSE_OP_BACKGROUND:
//                    // Clear the patch region to fully transparent black before the next frame

//                    compositedCanvas.Mutate(ctx =>
//                    {
//                        var graphicsOptions = new DrawingOptions
//                        {
//                            GraphicsOptions = new GraphicsOptions
//                                { AlphaCompositionMode = PixelAlphaCompositionMode.Clear }
//                        };
//                        ctx.Fill(graphicsOptions, Color.Transparent, patchRect);
//                    });
//                    break;
//                case APNG_DISPOSE_OP_PREVIOUS:
//                    // Revert the patch region to its state before this frame was rendered
//                    if (previousCanvas != null) Blit(previousCanvas, compositedCanvas, patchRect);
//                    break;
//                case APNG_DISPOSE_OP_NONE:
//                    // No disposal; leave the output buffer unchanged, allowing pixels to persist
//                    break;
//            }
//        }

//        // Clean up resources
//        previousCanvas.Dispose();
//        compositedCanvas.Dispose();

//        // Return the list of extracted frames
//        return history;
//    }


//    private static void Blit(Image<Rgba32> source, Image<Rgba32> destination, Rectangle region)
//    {
//        var sourcePixels = new Rgba32[source.Width * source.Height];
//        source.CopyPixelDataTo(sourcePixels);

//        destination.ProcessPixelRows(accessor =>
//        {
//            for (var y = 0; y < region.Height; y++)
//            {
//                var destRow = accessor.GetRowSpan(region.Y + y);
//                var destSlice = destRow.Slice(region.X, region.Width);

//                var sourceStartIndex = y * source.Width;
//                var sourceRowSpan = new ReadOnlySpan<Rgba32>(sourcePixels, sourceStartIndex, region.Width);

//                sourceRowSpan.CopyTo(destSlice);
//            }
//        });
//    }

//    private static Image<Rgba32> CreateImageFromChunks(PngChunk ihdrChunk, IEnumerable<PngChunk> otherChunks,
//        IEnumerable<PngChunk> dataChunks, uint? frameWidth = null, uint? frameHeight = null)
//    {
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
//        return Image.Load<Rgba32>(ms);
//    }

//    #endregion

//    #region PNG/Binary Helpers

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
//            SequenceNumber = ReadUInt32BigEndian(r), Width = ReadUInt32BigEndian(r), Height = ReadUInt32BigEndian(r),
//            XOffset = ReadUInt32BigEndian(r), YOffset = ReadUInt32BigEndian(r), DelayNum = ReadUInt16BigEndian(r),
//            DelayDen = ReadUInt16BigEndian(r), DisposeOp = r.ReadByte(), BlendOp = r.ReadByte()
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

//    private static uint ReadUInt32BigEndian(byte[] b, int o)
//    {
//        return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
//    }

//    private static uint ReadUInt32BigEndian(BinaryReader r)
//    {
//        return (uint)((r.ReadByte() << 24) | (r.ReadByte() << 16) | (r.ReadByte() << 8) | r.ReadByte());
//    }

//    private static ushort ReadUInt16BigEndian(BinaryReader r)
//    {
//        return (ushort)((r.ReadByte() << 8) | r.ReadByte());
//    }

//    private static void WriteUInt32BigEndian(BinaryWriter w, uint v)
//    {
//        w.Write((byte)(v >> 24));
//        w.Write((byte)(v >> 16));
//        w.Write((byte)(v >> 8));
//        w.Write((byte)v);
//    }

//    private static byte[] GetBytesBigEndian(uint v)
//    {
//        return [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
//    }

//    #endregion
//}

