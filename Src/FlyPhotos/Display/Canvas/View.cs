using System;
using System.Numerics;
using Windows.Foundation;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Canvas;

/// <summary>
/// The view of the image on screen, as an immutable value. <see cref="ViewController"/> owns the current one and
/// replaces it with <c>with</c> expressions; nothing else changes it.
/// </summary>
/// <param name="ImageRect">
/// The image in layout pixels, always (0, 0, W, H). A preview is stretched over the photo's full size, so 100% means
/// one photo pixel per screen pixel on the preview and on the HQ alike.
/// </param>
/// <param name="Scale">Screen pixels per layout pixel; 1 is 100%.</param>
/// <param name="Pos">Where the image's centre is on screen, in canvas pixels.</param>
/// <param name="Rotation">Degrees, in 90° steps: the EXIF rotation plus the user's. Not normalized (may be negative or ≥ 360).</param>
internal readonly record struct View(Rect ImageRect, float Scale, Point Pos, int Rotation)
{
    /// <summary>The image's size in layout pixels, unrotated.</summary>
    public Size ImageSize => new(ImageRect.Width, ImageRect.Height);

    /// <summary>
    /// Layout pixels to screen pixels. Snapped, the translation lands on whole pixels, which keeps 100% crisp (and
    /// avoids an NVIDIA glitch, #55); animations don't snap, or the settle would step in 1 px stairs.
    /// </summary>
    public Matrix3x2 Matrix(bool snap)
    {
        var m = LayoutToScreen(Scale, Pos);
        if (snap)
        {
            m.M31 = MathF.Round(m.M31);
            m.M32 = MathF.Round(m.M32);
        }
        return m;
    }

    /// <summary>
    /// <paramref name="pos"/> nudged by at most half a pixel so that this image at <paramref name="scale"/> rests on
    /// whole pixels. An animation aims at this, so it doesn't jump when it settles and snaps.
    /// </summary>
    public Point OnPixelGrid(float scale, Point pos)
    {
        var m = LayoutToScreen(scale, pos);
        return new Point(pos.X - (m.M31 - MathF.Round(m.M31)), pos.Y - (m.M32 - MathF.Round(m.M32)));
    }

    /// <summary>
    /// The frame a renderer draws with. <paramref name="offsetX"/> shifts it sideways without changing the view (the
    /// shrug), so a refused action can't move the user's pan.
    /// </summary>
    public ViewFrame Frame(bool snap, double offsetX = 0)
    {
        var mat = Matrix(snap);
        mat.M31 += (float)offsetX;
        Matrix3x2.Invert(mat, out var inv);
        return new ViewFrame(ImageRect, Scale, Rotation, mat, inv);
    }

    /// <summary>
    /// The image centre to the origin, then scale, rotate, and move to <paramref name="pos"/>. Rotating about the
    /// centre is what lets a 90° turn keep the image in place.
    /// </summary>
    private Matrix3x2 LayoutToScreen(float scale, Point pos) =>
        Matrix3x2.CreateTranslation((float)(-ImageRect.Width * 0.5f), (float)(-ImageRect.Height * 0.5f))
        * Matrix3x2.CreateScale(scale, scale)
        * RotationMatrix(Rotation)
        * Matrix3x2.CreateTranslation((float)pos.X, (float)pos.Y);

    /// <summary>The view's rotation step; <see cref="ViewMotion"/> pins image points with the same one.</summary>
    public static Matrix3x2 RotationMatrix(int degrees) => Matrix3x2.CreateRotation(MathF.PI * degrees / 180f);
}

/// <summary>
/// One frame of the view, as renderers and hit tests use it: a snapshot, so a renderer never sees the view change
/// halfway through a draw.
/// </summary>
/// <param name="ImageRect">The image in layout pixels, (0, 0, W, H).</param>
/// <param name="Scale">Screen pixels per layout pixel; renderers pick a mip level or a render size from it.</param>
/// <param name="Rotation">Degrees, as in <see cref="View.Rotation"/>.</param>
/// <param name="Mat">Layout pixels to canvas pixels, including any snap and shrug offset.</param>
/// <param name="MatInv">Canvas pixels back to layout pixels (hit tests, the visible part of the image).</param>
internal readonly record struct ViewFrame(Rect ImageRect, float Scale, int Rotation, Matrix3x2 Mat, Matrix3x2 MatInv)
{
    /// <summary>An image is laid out; before the first photo the rect is empty.</summary>
    public bool HasImage => ImageRect.Width > 0 && ImageRect.Height > 0;

    /// <summary>Canvas point <paramref name="canvasPoint"/> falls on the image.</summary>
    public bool Contains(Point canvasPoint)
    {
        if (!HasImage) return false;
        var p = Vector2.Transform(new Vector2((float)canvasPoint.X, (float)canvasPoint.Y), MatInv);
        return p.X >= ImageRect.X && p.Y >= ImageRect.Y && p.X <= ImageRect.Right && p.Y <= ImageRect.Bottom;
    }

    /// <summary>The image's bounds on the canvas, in canvas pixels. Rotation is in 90° steps, so two opposite corners span it.</summary>
    public Rect ScreenBounds()
    {
        var a = Vector2.Transform(new Vector2((float)ImageRect.Left, (float)ImageRect.Top), Mat);
        var b = Vector2.Transform(new Vector2((float)ImageRect.Right, (float)ImageRect.Bottom), Mat);
        return new Rect(new Point(a.X, a.Y), new Point(b.X, b.Y));
    }
}
