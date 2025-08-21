using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Buffer = System.Buffer;

namespace FlyPhotos.Controllers.Animators;

/// <summary>
/// A real-time animator for APNG files, designed to mirror the logic and API
/// of a GIF animator. It composites frames on-demand based on elapsed time,
/// providing an efficient way to play complex animations in a Win2D context.
/// </summary>
public class PngAnimator : IAnimator
{
    #region Nested Classes and Constants

    // Constants for APNG specification values.
    private const byte APNG_DISPOSE_OP_NONE = 0;
    private const byte APNG_DISPOSE_OP_BACKGROUND = 1;
    private const byte APNG_DISPOSE_OP_PREVIOUS = 2;
    private const byte APNG_BLEND_OP_SOURCE = 0;
    private const byte APNG_BLEND_OP_OVER = 1;

    // Stores pre-parsed metadata for a single animation frame.
    private class ApngFrameMetadata
    {
        public TimeSpan Delay { get; init; }
        public Rect Bounds { get; init; }
        public byte DisposeOp { get; init; }
        public byte BlendOp { get; init; }
        public List<Parser.PngChunk> FrameDataChunks { get; init; }
    }

    #endregion

    #region Fields and Properties

    public uint PixelWidth { get; }
    public uint PixelHeight { get; }
    public ICanvasImage Surface => _compositedSurface;

    // APNG structure data, held for the lifetime of the animator
    private readonly IRandomAccessStream _stream;
    private readonly Parser.PngChunk _ihdrChunk;
    private readonly List<Parser.PngChunk> _globalChunks;
    private readonly IReadOnlyList<ApngFrameMetadata> _frameMetadata;
    private readonly TimeSpan _totalAnimationDuration;
    private readonly ICanvasResourceCreator _device;

    // Off-screen surfaces for composing frames
    private readonly CanvasRenderTarget _compositedSurface;
    private readonly CanvasRenderTarget _previousFrameBackup; // For DISPOSE_OP_PREVIOUS

    // State for rendering logic
    private int _currentFrameIndex = -1;
    private Rect _previousFrameRect = Rect.Empty;
    private byte _previousFrameDisposal = APNG_DISPOSE_OP_NONE;

    #endregion

    #region Creation and Initialization

    private PngAnimator(
        ICanvasResourceCreator device,
        IRandomAccessStream stream,
        Parser.ApngData apngData,
        List<ApngFrameMetadata> metadata)
    {
        _device = device;
        _stream = stream;
        _ihdrChunk = apngData.IhdrChunk;
        _globalChunks = apngData.GlobalChunks;
        _frameMetadata = metadata;
        _totalAnimationDuration = TimeSpan.FromMilliseconds(metadata.Sum(m => m.Delay.TotalMilliseconds));

        PixelWidth = apngData.CanvasWidth;
        PixelHeight = apngData.CanvasHeight;

        _compositedSurface = new CanvasRenderTarget(_device, PixelWidth, PixelHeight, 96);
        _previousFrameBackup = new CanvasRenderTarget(_device, PixelWidth, PixelHeight, 96);

        // If the first frame is not the default image, render it immediately.
        if (!apngData.IsDefaultImageFirstFrame)
        {
            using var ds = _compositedSurface.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
        }
        else
        {
            // If the default image IS the first frame, we need to draw it.
            // This is a special case handled by the parser's metadata for frame 0.
            // We force an initial render of frame 0.
            Task.Run(async () => await RenderFrameAsync(0)).Wait();
            _currentFrameIndex = 0;
        }
    }

    public static async Task<PngAnimator> CreateAsync(byte[] apngData, ICanvasResourceCreator device)
    {
        var memoryStream = new MemoryStream(apngData);
        var randomAccessStream = memoryStream.AsRandomAccessStream();
        return await CreateAsyncInternal(randomAccessStream, device);
    }

    private static async Task<PngAnimator> CreateAsyncInternal(IRandomAccessStream stream,
        ICanvasResourceCreator device)
    {
        try
        {
            // 1. Parse the entire APNG structure first.
            var apngData = await Parser.ParseApngStreamAsync(stream);
            if (apngData.FrameControls.Count == 0)
            {
                throw new ArgumentException("APNG file contains no animation frames.");
            }

            // 2. Convert parser's FrameControl objects into our internal ApngFrameMetadata.
            var metadata = new List<ApngFrameMetadata>();
            foreach (var fc in apngData.FrameControls)
            {
                // Calculate delay. If denominator is 0, spec says treat numerator as milliseconds.
                double delayMs = 100.0; // Default delay
                if (fc.DelayNum > 0)
                {
                    delayMs = fc.DelayDen == 0 ? fc.DelayNum : (double)fc.DelayNum / fc.DelayDen * 1000.0;
                }

                metadata.Add(new ApngFrameMetadata
                {
                    Delay = TimeSpan.FromMilliseconds(delayMs),
                    Bounds = new Rect(fc.XOffset, fc.YOffset, fc.Width, fc.Height),
                    DisposeOp = fc.DisposeOp,
                    BlendOp = fc.BlendOp,
                    FrameDataChunks = fc.FrameDataChunks
                });
            }

            return new PngAnimator(device, stream, apngData, metadata);
        }
        catch (Exception)
        {
            // If creation fails, we are responsible for the stream.
            stream.Dispose();
            throw;
        }
    }

    #endregion

    #region Animation Logic

    public async Task UpdateAsync(TimeSpan totalElapsedTime)
    {
        if (_totalAnimationDuration == TimeSpan.Zero) return;

        var elapsedInLoop = TimeSpan.FromTicks(totalElapsedTime.Ticks % _totalAnimationDuration.Ticks);

        int targetFrameIndex = 0;
        var accumulatedTime = TimeSpan.Zero;
        for (int i = 0; i < _frameMetadata.Count; i++)
        {
            accumulatedTime += _frameMetadata[i].Delay;
            if (elapsedInLoop < accumulatedTime)
            {
                targetFrameIndex = i;
                break;
            }
        }

        if (targetFrameIndex < _currentFrameIndex)
        {
            _currentFrameIndex = -1;
            _previousFrameDisposal = APNG_DISPOSE_OP_NONE;
            _previousFrameRect = Rect.Empty;

            using var ds = _compositedSurface.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
        }

        if (targetFrameIndex > _currentFrameIndex)
        {
            for (int i = _currentFrameIndex + 1; i <= targetFrameIndex; i++)
            {
                await RenderFrameAsync(i);
            }
        }
        _currentFrameIndex = targetFrameIndex;
    }

    private async Task RenderFrameAsync(int frameIndex)
    {
        var metadata = _frameMetadata[frameIndex];

        // --- AWAIT FIRST ---
        // Create the patch bitmap for the current frame before opening a DrawingSession.
        using var patchBitmap = await CreatePatchBitmapAsync(metadata);

        // --- THEN DRAW ---

        // 2. Prepare for NEXT frame's disposal.
        // If the CURRENT frame's disposal is PREVIOUS, we back up the canvas's current state.
        if (metadata.DisposeOp == APNG_DISPOSE_OP_PREVIOUS)
        {
            var backupRegion = new Rect(0, 0, _compositedSurface.Size.Width, _compositedSurface.Size.Height);
            using var backupDs = _previousFrameBackup.CreateDrawingSession();
            backupDs.DrawImage(_compositedSurface, backupRegion, backupRegion);
        }

        // Now, perform all drawing for the current frame in a single, atomic session.
        using (var ds = _compositedSurface.CreateDrawingSession())
        {
            switch (_previousFrameDisposal)
            {
                case APNG_DISPOSE_OP_BACKGROUND:
                    ds.Blend = CanvasBlend.Copy;
                    ds.FillRectangle(_previousFrameRect, Colors.Transparent);
                    ds.Blend = CanvasBlend.SourceOver;
                    break;
                case APNG_DISPOSE_OP_PREVIOUS:
                    ds.DrawImage(_previousFrameBackup, _previousFrameRect, _previousFrameRect);
                    break;
            }

            switch (metadata.BlendOp)
            {
                // 3. Draw the CURRENT frame. This is where APNG's BlendOp matters.
                case APNG_BLEND_OP_SOURCE:
                    ds.Blend = CanvasBlend.Copy;
                    ds.FillRectangle(metadata.Bounds, Colors.Transparent);
                    ds.Blend = CanvasBlend.SourceOver;
                    ds.DrawImage(patchBitmap, (float)metadata.Bounds.X, (float)metadata.Bounds.Y);
                    break;
                case APNG_BLEND_OP_OVER:
                    ds.DrawImage(patchBitmap, (float)metadata.Bounds.X, (float)metadata.Bounds.Y);
                    break;
            }
        }

        // 4. Update state for the next iteration.
        _previousFrameRect = metadata.Bounds;
        _previousFrameDisposal = metadata.DisposeOp;
    }

    private Task<CanvasBitmap> CreatePatchBitmapAsync(ApngFrameMetadata metadata)
    {
        // This is a lightweight, on-demand version of the extractor's "CreateImageFromChunks".
        return Parser.CreateImageFromChunksAsync(
            _device,
            _ihdrChunk,
            _globalChunks,
            metadata.FrameDataChunks,
            (uint)metadata.Bounds.Width,
            (uint)metadata.Bounds.Height);
    }

    #endregion

    public void Dispose()
    {
        _compositedSurface?.Dispose();
        _previousFrameBackup?.Dispose();
        _stream?.Dispose();
    }

    #region Private APNG Parser

    /// <summary>
    /// A self-contained static parser that reads an APNG stream and extracts its
    /// structural components without performing any rendering.
    /// </summary>
    private static class Parser
    {
        public class PngChunk { public string Type; public byte[] Data; }
        public class FrameControl
        {
            public uint SequenceNumber, Width, Height, XOffset, YOffset;
            public ushort DelayNum, DelayDen;
            public byte DisposeOp, BlendOp;
            public List<PngChunk> FrameDataChunks { get; } = [];
        }

        public class ApngData
        {
            public PngChunk IhdrChunk { get; init; }
            public uint CanvasWidth { get; init; }
            public uint CanvasHeight { get; init; }
            public List<PngChunk> GlobalChunks { get; init; }
            public List<FrameControl> FrameControls { get; init; }
            public bool IsDefaultImageFirstFrame { get; init; }
        }

        private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        private const string AcTL = "acTL", FcTL = "fcTL", FdAT = "fdAT";

        public static Task<ApngData> ParseApngStreamAsync(IRandomAccessStream stream)
        {
            // Parsing is CPU-bound, so run it on a background thread.
            return Task.Run(() =>
            {
                stream.Seek(0);
                using var reader = new BinaryReader(stream.AsStreamForRead());

                if (!reader.ReadBytes(PngSignature.Length).SequenceEqual(PngSignature))
                    throw new ArgumentException("Not a valid PNG file.");

                var chunks = ReadAllChunks(reader);
                var globalChunks = new List<PngChunk>();
                var defaultImageIdatChunks = new List<PngChunk>();
                var frameControls = new List<FrameControl>();
                PngChunk ihdrChunk = null;

                foreach (var chunk in chunks)
                {
                    switch (chunk.Type)
                    {
                        case "IHDR": ihdrChunk = chunk; globalChunks.Add(chunk); break;
                        case AcTL: break; // Just confirms it's an APNG
                        case FcTL: frameControls.Add(ParseFrameControl(chunk)); break;
                        case FdAT:
                            if (frameControls.Any()) frameControls.Last().FrameDataChunks.Add(ConvertFdatToIdat(chunk));
                            break;
                        case "IDAT":
                            if (!frameControls.Any()) defaultImageIdatChunks.Add(chunk);
                            else frameControls.Last().FrameDataChunks.Add(chunk);
                            break;
                        default:
                            if (!frameControls.Any() && chunk.Type != "IEND") globalChunks.Add(chunk);
                            break;
                    }
                }

                if (ihdrChunk == null) throw new InvalidDataException("IHDR chunk not found.");

                var orderedFrames = frameControls.OrderBy(fc => fc.SequenceNumber).ToList();
                bool defaultImageIsFirstFrame = false;

                // If the default image exists, check if it's used as the first frame.
                if (defaultImageIdatChunks.Any())
                {
                    var firstFrameControl = orderedFrames.FirstOrDefault();
                    if (firstFrameControl?.SequenceNumber == 0)
                    {
                        // The default IDATs belong to the first frame.
                        firstFrameControl.FrameDataChunks.InsertRange(0, defaultImageIdatChunks);
                        defaultImageIsFirstFrame = true;
                    }
                }

                return new ApngData
                {
                    IhdrChunk = ihdrChunk,
                    CanvasWidth = ReadUInt32BigEndian(ihdrChunk.Data, 0),
                    CanvasHeight = ReadUInt32BigEndian(ihdrChunk.Data, 4),
                    GlobalChunks = globalChunks.Where(c => c.Type != "IHDR").ToList(),
                    FrameControls = orderedFrames,
                    IsDefaultImageFirstFrame = defaultImageIsFirstFrame
                };
            });
        }

        public static async Task<CanvasBitmap> CreateImageFromChunksAsync(ICanvasResourceCreator resourceCreator, PngChunk ihdrChunk, IEnumerable<PngChunk> otherChunks,
            IEnumerable<PngChunk> dataChunks, uint? frameWidth = null, uint? frameHeight = null)
        {
            using var ms = new InMemoryRandomAccessStream();
            await using var writer = new BinaryWriter(ms.AsStreamForWrite());
            writer.Write(PngSignature);
            var ihdrData = (byte[])ihdrChunk.Data.Clone();
            if (frameWidth.HasValue && frameHeight.HasValue)
            {
                Array.Copy(GetBytesBigEndian(frameWidth.Value), 0, ihdrData, 0, 4);
                Array.Copy(GetBytesBigEndian(frameHeight.Value), 0, ihdrData, 4, 4);
            }

            WriteChunk(writer, "IHDR", ihdrData);
            foreach (var chunk in otherChunks) WriteChunk(writer, chunk.Type, chunk.Data);
            foreach (var chunk in dataChunks) WriteChunk(writer, chunk.Type, chunk.Data);
            WriteChunk(writer, "IEND", []);

            await writer.BaseStream.FlushAsync();
            ms.Seek(0);

            return await CanvasBitmap.LoadAsync(resourceCreator, ms);
        }

        private static List<PngChunk> ReadAllChunks(BinaryReader r)
        {
            var c = new List<PngChunk>();
            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                try
                {
                    var len = ReadUInt32BigEndian(r);
                    var type = Encoding.ASCII.GetString(r.ReadBytes(4));
                    var data = r.ReadBytes((int)len);
                    ReadUInt32BigEndian(r); // Skip CRC
                    c.Add(new PngChunk { Type = type, Data = data });
                    if (type == "IEND") break;
                }
                catch (EndOfStreamException) { break; }
            }
            return c;
        }

        private static FrameControl ParseFrameControl(PngChunk fc)
        {
            using var r = new BinaryReader(new MemoryStream(fc.Data));
            return new FrameControl
            {
                SequenceNumber = ReadUInt32BigEndian(r),
                Width = ReadUInt32BigEndian(r),
                Height = ReadUInt32BigEndian(r),
                XOffset = ReadUInt32BigEndian(r),
                YOffset = ReadUInt32BigEndian(r),
                DelayNum = ReadUInt16BigEndian(r),
                DelayDen = ReadUInt16BigEndian(r),
                DisposeOp = r.ReadByte(),
                BlendOp = r.ReadByte()
            };
        }

        private static PngChunk ConvertFdatToIdat(PngChunk f)
        {
            var d = new byte[f.Data.Length - 4];
            Array.Copy(f.Data, 4, d, 0, d.Length);
            return new PngChunk { Type = "IDAT", Data = d };
        }

        private static void WriteChunk(BinaryWriter w, string t, byte[] d)
        {
            var typeBytes = Encoding.ASCII.GetBytes(t);
            var crcBytes = new byte[typeBytes.Length + d.Length];
            Buffer.BlockCopy(typeBytes, 0, crcBytes, 0, typeBytes.Length);
            if (d.Length > 0) Buffer.BlockCopy(d, 0, crcBytes, typeBytes.Length, d.Length);

            WriteUInt32BigEndian(w, (uint)d.Length);
            w.Write(typeBytes);
            if (d.Length > 0) w.Write(d);
            WriteUInt32BigEndian(w, Crc32.Compute(crcBytes));
        }

        private static uint ReadUInt32BigEndian(byte[] b, int o) => (uint)b[o] << 24 | (uint)b[o + 1] << 16 | (uint)b[o + 2] << 8 | b[o + 3];
        private static uint ReadUInt32BigEndian(BinaryReader r) => (uint)(r.ReadByte() << 24 | r.ReadByte() << 16 | r.ReadByte() << 8 | r.ReadByte());
        private static ushort ReadUInt16BigEndian(BinaryReader r) => (ushort)(r.ReadByte() << 8 | r.ReadByte());
        private static void WriteUInt32BigEndian(BinaryWriter w, uint v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
        private static byte[] GetBytesBigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }
    #endregion
}

