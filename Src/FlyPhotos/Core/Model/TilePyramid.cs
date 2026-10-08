using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;

namespace FlyPhotos.Core.Model;

/// <summary>
/// One resolution level of a tiled image, held in CPU memory. The image is cut into <see cref="Size"/>²
/// tiles, each stored as its own array (so no single buffer hits the 2 GB .NET array limit) with a
/// <see cref="Gutter"/>-pixel border of duplicated neighbour pixels, clamped at the image edge. The
/// gutter lets the GPU sample across tile edges without seams.
/// Tile (c, r) holds image pixels x ∈ [c·Size − Gutter, (c+1)·Size + Gutter), same for y.
/// </summary>
internal sealed class TileLevel
{
    /// <summary>Edge length of a tile's image area, in pixels.</summary>
    public const int Size = 1024;
    /// <summary>Width of the duplicated border around each tile, in pixels.</summary>
    public const int Gutter = 2;
    /// <summary>Edge length of a stored tile, gutter included; also its row length in pixels.</summary>
    public const int Stride = Size + 2 * Gutter;

    /// <summary>Width of this level's image, in pixels.</summary>
    public int Width { get; }
    /// <summary>Height of this level's image, in pixels.</summary>
    public int Height { get; }
    /// <summary>Number of tile columns.</summary>
    public int Cols { get; }
    /// <summary>Number of tile rows.</summary>
    public int Rows { get; }
    /// <summary>Tiles in row-major order, each <see cref="Stride"/>² pixels at 4 bytes per pixel.</summary>
    private readonly byte[][] _tiles;

    /// <summary>Allocates the tiles for a <paramref name="width"/>×<paramref name="height"/> image, unfilled.</summary>
    public TileLevel(int width, int height)
    {
        Width = width;
        Height = height;
        Cols = (width + Size - 1) / Size;
        Rows = (height + Size - 1) / Size;
        _tiles = new byte[Cols * Rows][];
        // Not zeroed: FillTileRow writes every byte, and Downsample2x writes everything that is ever
        // sampled (image + gutter); an edge tile's unwritten remainder is never read.
        for (var i = 0; i < _tiles.Length; i++)
            _tiles[i] = GC.AllocateUninitializedArray<byte>(Stride * Stride * 4);
    }

    /// <summary>The pixels of tile (<paramref name="col"/>, <paramref name="row"/>), gutter included.</summary>
    public byte[] Tile(int col, int row) => _tiles[row * Cols + col];

    /// <summary>
    /// Image rows a strip must cover to fill tile-row <paramref name="row"/> (see <see cref="FillTileRow"/>):
    /// the tile's rows plus gutters, clamped to the image.
    /// </summary>
    public (int Y0, int Rows) StripFor(int row)
    {
        var y0 = Math.Max(0, row * Size - Gutter);
        var y1 = Math.Min(Height, (row + 1) * Size + Gutter);
        return (y0, y1 - y0);
    }

    /// <summary>Fills every tile from one buffer holding the whole image (4 bytes per pixel, rows packed).</summary>
    public void FillAll(ReadOnlySpan<byte> pixels)
    {
        for (var row = 0; row < Rows; row++)
            FillTileRow(row, pixels, 0);
    }

    /// <summary>
    /// Fills every tile of tile-row <paramref name="row"/> from a strip of whole image rows (4 bytes per
    /// pixel, rows packed). The strip must cover image rows [row·Size − Gutter, (row+1)·Size + Gutter)
    /// clamped to the image; <paramref name="stripY0"/> is the image row of the strip's first row.
    /// </summary>
    public void FillTileRow(int row, ReadOnlySpan<byte> strip, int stripY0)
    {
        var rowBytes = Width * 4;
        for (var ly = 0; ly < Stride; ly++)
        {
            var srcY = Math.Clamp(row * Size - Gutter + ly, 0, Height - 1);
            var src = strip.Slice((srcY - stripY0) * rowBytes, rowBytes);
            for (var col = 0; col < Cols; col++)
            {
                var dst = Tile(col, row).AsSpan(ly * Stride * 4, Stride * 4);
                var x0 = col * Size - Gutter;
                if (x0 >= 0 && x0 + Stride <= Width)
                {
                    src.Slice(x0 * 4, Stride * 4).CopyTo(dst);
                    continue;
                }
                // Edge tile: clamp each pixel into the image.
                for (var lx = 0; lx < Stride; lx++)
                    src.Slice(Math.Clamp(x0 + lx, 0, Width - 1) * 4, 4).CopyTo(dst.Slice(lx * 4, 4));
            }
        }
    }

    /// <summary>
    /// Copies image row <paramref name="y"/>, pixels [x0, x0 + count), into <paramref name="dst"/>, clamping
    /// coordinates to the image. Copies whole in-tile runs; only out-of-image pixels go one at a time.
    /// </summary>
    private void CopyRow(int y, int x0, int count, Span<byte> dst)
    {
        y = Math.Clamp(y, 0, Height - 1);
        int row = y / Size, ly = y - row * Size + Gutter;
        for (var i = 0; i < count;)
        {
            var x = x0 + i;
            var cx = Math.Clamp(x, 0, Width - 1);
            int col = cx / Size, lx = cx - col * Size + Gutter;
            // Inside the image: run to the end of this tile's core or of the image. Outside: one clamped pixel.
            var n = x == cx ? Math.Min(count - i, Math.Min((col + 1) * Size, Width) - x) : 1;
            Tile(col, row).AsSpan((ly * Stride + lx) * 4, n * 4).CopyTo(dst.Slice(i * 4));
            i += n;
        }
    }

    /// <summary>
    /// Half-size level via a 2×2 box filter (odd sizes round up), built in parallel over tiles. Channel
    /// order doesn't matter — it averages bytes.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled via <paramref name="ct"/>.</exception>
    public TileLevel Downsample2x(CancellationToken ct = default)
    {
        var dst = new TileLevel((Width + 1) / 2, (Height + 1) / 2);
        const int srcCount = 2 * Stride; // source pixels per destination tile row
        Parallel.For(0, dst._tiles.Length, new ParallelOptions { CancellationToken = ct },
            () => (A: new byte[srcCount * 4], B: new byte[srcCount * 4]),
            (t, _, rows) =>
            {
                int col = t % dst.Cols, row = t / dst.Cols;
                var tile = dst._tiles[t];
                var srcX0 = 2 * (col * Size - Gutter);
                // Only the image area plus its gutter is ever sampled; an edge tile's remainder is left unwritten.
                var lxEnd = Math.Min(Stride, dst.Width - col * Size + 2 * Gutter);
                var lyEnd = Math.Min(Stride, dst.Height - row * Size + 2 * Gutter);
                for (var ly = 0; ly < lyEnd; ly++)
                {
                    // Gutter pixels beyond the image edge sample clamped source directly rather than repeating
                    // the destination edge pixel; they differ slightly but only feed sampling outside the image.
                    var srcY = 2 * (row * Size - Gutter + ly);
                    CopyRow(srcY, srcX0, 2 * lxEnd, rows.A);
                    CopyRow(srcY + 1, srcX0, 2 * lxEnd, rows.B);
                    var o = ly * Stride * 4;
                    for (var i = 0; i < lxEnd * 4; i++)
                    {
                        var s = (i >> 2) * 8 + (i & 3); // left source pixel's channel; right one is s + 4
                        tile[o + i] = (byte)((rows.A[s] + rows.A[s + 4] + rows.B[s] + rows.B[s + 4] + 2) >> 2);
                    }
                }
                return rows;
            },
            _ => { });
        return dst;
    }

    /// <summary>Copies the image pixels (no gutter) of a single-tile level into a packed buffer.</summary>
    public byte[] ToPackedPixels()
    {
        if (Cols != 1 || Rows != 1) throw new InvalidOperationException("Only a single-tile level can be packed.");
        var packed = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
            _tiles[0].AsSpan(((y + Gutter) * Stride + Gutter) * 4, Width * 4)
                .CopyTo(packed.AsSpan(y * Width * 4));
        return packed;
    }
}

/// <summary>
/// Mip pyramid of <see cref="TileLevel"/>s for images too large for one GPU bitmap. Level 0 is full
/// resolution; each next level is half the size, down to the first level that fits in a single tile.
/// </summary>
internal sealed class TilePyramid
{
    /// <summary>Levels from full resolution (index 0) to the single-tile level (last).</summary>
    public TileLevel[] Levels { get; }
    /// <summary>Pixel format of every tile: BGRA from WIC/Magick, RGBA from libheif. Always premultiplied.</summary>
    public DirectXPixelFormat Format { get; }
    /// <summary>Width of the full-resolution image, in pixels.</summary>
    public int FullWidth => Levels[0].Width;
    /// <summary>Height of the full-resolution image, in pixels.</summary>
    public int FullHeight => Levels[0].Height;

    /// <summary>Use <see cref="Build"/>.</summary>
    private TilePyramid(TileLevel[] levels, DirectXPixelFormat format)
    {
        Levels = levels;
        Format = format;
    }

    /// <summary>
    /// Builds the smaller levels from a filled <paramref name="level0"/> by repeated halving until one fits
    /// a single tile.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled while downsampling.</exception>
    public static TilePyramid Build(TileLevel level0, DirectXPixelFormat format, CancellationToken ct = default)
    {
        var levels = new List<TileLevel> { level0 };
        while (levels[^1].Cols > 1 || levels[^1].Rows > 1)
            levels.Add(levels[^1].Downsample2x(ct));
        return new TilePyramid([.. levels], format);
    }
}
