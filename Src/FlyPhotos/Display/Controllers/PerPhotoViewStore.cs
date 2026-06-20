using System;
using System.Collections.Generic;
using Windows.Foundation;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Per-session memory of user-modified views, keyed by photo file path, used only by the
/// "RememberPerPhoto" navigation mode. Stores each view in a deliberately *relative* form so it restores
/// correctly even when conditions differ between caching and restoring:
/// <list type="bullet">
///   <item>pan is kept as an offset from the canvas centre divided by canvas size (not absolute pixels), so
///     it survives window resizing;</item>
///   <item>rotation is kept as the user's manual rotation only (total minus the image's EXIF baseline), so
///     re-applying it onto a different baseline — e.g. a pre-oriented preview thumbnail vs. an HQ image whose
///     EXIF rotation is applied at render — never double-counts the EXIF orientation.</item>
/// </list>
/// The store owns the normalize / de-normalize math and the dictionary; <see cref="CanvasViewManager"/>
/// decides <i>when</i> to save (mode + user-modified) and applies the restored values to the live view.
/// </summary>
internal sealed class PerPhotoViewStore
{
    private readonly record struct PerPhotoViewState(
        float Scale, float LastScaleTo, Point NormalizedPan, int UserRotation);

    private readonly Dictionary<string, PerPhotoViewState> _cache = [];

    /// <summary>A remembered view de-normalized for the current canvas and EXIF baseline.</summary>
    public readonly record struct RestoredView(
        float Scale, float LastScaleTo, Point ImagePos, int Rotation, bool PanIsCentered);

    /// <summary>
    /// Saves the current view for <paramref name="photoPath"/>, normalized against <paramref name="canvasSize"/>
    /// and <paramref name="originalImageRotation"/> (the EXIF baseline) so it survives resize and baseline
    /// changes. The caller is responsible for only calling this when the view is worth remembering.
    /// </summary>
    public void Save(string photoPath, float scale, float lastScaleTo, Point imagePos, int rotation,
                     int originalImageRotation, Size canvasSize)
    {
        // Convert the absolute pan to a canvas-size-independent offset from the centre.
        var panOffsetX = imagePos.X - canvasSize.Width / 2.0;
        var panOffsetY = imagePos.Y - canvasSize.Height / 2.0;
        var normalizedPanX = canvasSize.Width > 0 ? panOffsetX / canvasSize.Width : 0; // guard div-by-zero
        var normalizedPanY = canvasSize.Height > 0 ? panOffsetY / canvasSize.Height : 0;

        // Strip the EXIF baseline so only the user's manual rotation is remembered; TryRestore re-bases it
        // onto whatever baseline the image presents on the next visit.
        var userRotation = rotation - originalImageRotation;

        _cache[photoPath] = new PerPhotoViewState(
            scale, lastScaleTo, new Point(normalizedPanX, normalizedPanY), userRotation);
    }

    /// <summary>
    /// If a view is remembered for <paramref name="photoPath"/>, de-normalizes it for the current
    /// <paramref name="canvasSize"/> and <paramref name="originalImageRotation"/> baseline and returns true.
    /// Re-basing the user rotation onto the just-set baseline keeps a subsequent Preview → HQ rotation delta
    /// netting to zero even when the preview and HQ baselines differ.
    /// </summary>
    public bool TryRestore(string photoPath, int originalImageRotation, Size canvasSize, out RestoredView view)
    {
        if (!_cache.TryGetValue(photoPath, out var s))
        {
            view = default;
            return false;
        }

        // Re-hydrate the absolute pan position from the normalized offset for the current canvas size.
        var panOffsetX = s.NormalizedPan.X * canvasSize.Width;
        var panOffsetY = s.NormalizedPan.Y * canvasSize.Height;
        var imagePos = new Point(canvasSize.Width / 2.0 + panOffsetX, canvasSize.Height / 2.0 + panOffsetY);

        // A panned-at-fit-scale photo is not "fitted", so the caller needs to know the pan was centred.
        var panIsCentered = Math.Abs(s.NormalizedPan.X) < 0.001 && Math.Abs(s.NormalizedPan.Y) < 0.001;

        view = new RestoredView(s.Scale, s.LastScaleTo, imagePos,
                                originalImageRotation + s.UserRotation, panIsCentered);
        return true;
    }

    /// <summary>Drops any remembered view for <paramref name="photoPath"/> (e.g. it returned to default).</summary>
    public void Remove(string photoPath) => _cache.Remove(photoPath);
}
