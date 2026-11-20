using System;
using System.Collections.Generic;
using FlyPhotos.Display.Controllers.Animation;
using Windows.Foundation;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Per-session memory of user-modified views, keyed by photo file path, used only by the
/// "RememberPerPhoto" navigation mode. Stores each view in a deliberately *relative* form so it restores
/// correctly even when conditions differ between caching and restoring:
/// <list type="bullet">
///   <item>pan is kept as an offset from the canvas centre divided by canvas size (not absolute pixels), so
///     it survives window resizing (<see cref="ZoomGeometry.NormalizedPan"/>);</item>
///   <item>rotation is kept as the user's manual rotation only (total minus the image's EXIF baseline), so
///     re-applying it onto a different baseline — e.g. a pre-oriented preview thumbnail vs. an HQ image whose
///     EXIF rotation is applied at render — never double-counts the EXIF orientation;</item>
///   <item>the image's on-screen size is kept with it, so a view saved on the HQ and restored onto a preview laid out
///     at another size (a RAW's metadata size vs. its decoded size, #195) has the same part of the photo at the
///     centre; the HQ then replaces the preview without moving it.</item>
/// </list>
/// The scale is absolute, and saved at rest (no animation running), so it is also the target scale.
/// <see cref="CanvasViewManager"/> decides <i>when</i> to save and applies the restored values to the live view.
/// </summary>
internal sealed class PerPhotoViewStore
{
    private readonly record struct PerPhotoViewState(float Scale, Point NormalizedPan, int UserRotation,
        (double Width, double Height) OnScreenSize);

    private readonly Dictionary<string, PerPhotoViewState> _cache = [];

    /// <summary>A remembered view de-normalized for the current canvas and EXIF baseline.</summary>
    public readonly record struct RestoredView(float Scale, Point ImagePos, int Rotation, bool PanIsCentered);

    /// <summary>
    /// Saves the view for <paramref name="photoPath"/>, normalized against <paramref name="canvasSize"/> and
    /// <paramref name="exifRotation"/> so it survives resize and baseline changes, with the size of the image it was
    /// made on (<paramref name="imageSize"/>, in layout pixels). The caller only calls this when the view is worth
    /// remembering.
    /// </summary>
    public void Save(string photoPath, float scale, Point imagePos, int rotation, int exifRotation, Size canvasSize,
        Size imageSize) =>
        _cache[photoPath] = new PerPhotoViewState(scale, ZoomGeometry.NormalizedPan(imagePos, canvasSize),
            rotation - exifRotation, ZoomGeometry.OnScreenSize(imageSize, rotation));

    /// <summary>
    /// If a view is remembered for <paramref name="photoPath"/>, de-normalizes it for the current
    /// <paramref name="canvasSize"/>, <paramref name="exifRotation"/> baseline and <paramref name="imageSize"/> and
    /// returns true. Re-basing the user rotation onto the just-set baseline keeps a subsequent Preview → HQ rotation
    /// delta netting to zero even when the preview and HQ baselines differ.
    /// </summary>
    public bool TryRestore(string photoPath, int exifRotation, Size canvasSize, Size imageSize, out RestoredView view)
    {
        if (!_cache.TryGetValue(photoPath, out var s))
        {
            view = default;
            return false;
        }

        var rotation = exifRotation + s.UserRotation;
        var pos = ZoomGeometry.PanFromNormalized(s.NormalizedPan, canvasSize);
        // The offset from the centre scales with the image's on-screen size: the same part of the photo at the centre.
        var (w, h) = ZoomGeometry.OnScreenSize(imageSize, rotation);
        if (s.OnScreenSize.Width > 0 && s.OnScreenSize.Height > 0)
            pos = ZoomGeometry.ScaleAbout(pos, ZoomGeometry.Centre(canvasSize),
                w / s.OnScreenSize.Width, h / s.OnScreenSize.Height);
        // A panned-at-fit-scale photo is not "fitted", so the caller needs to know the pan was centred.
        view = new RestoredView(s.Scale, pos, rotation, ZoomGeometry.IsCentred(pos, canvasSize));
        return true;
    }

    /// <summary>Drops any remembered view for <paramref name="photoPath"/> (e.g. it returned to default).</summary>
    public void Remove(string photoPath) => _cache.Remove(photoPath);

    /// <summary>Moves any remembered view from <paramref name="oldPath"/> to <paramref name="newPath"/> (the photo was renamed).</summary>
    public void Rename(string oldPath, string newPath)
    {
        if (_cache.Remove(oldPath, out var state))
            _cache[newPath] = state;
    }
}
