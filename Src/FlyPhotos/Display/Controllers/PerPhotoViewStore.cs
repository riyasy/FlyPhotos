using System.Collections.Generic;
using FlyPhotos.Display.Controllers.Animation;
using Windows.Foundation;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Per-session memory of user-modified views, keyed by photo file path, used only by the
/// "RememberPerPhoto" navigation mode. Stores each view in a deliberately *relative* form so it restores
/// correctly even when conditions differ between caching and restoring:
/// <list type="bullet">
///   <item>zoom is kept on the photo itself (screen pixels per pixel of the photo by its metadata), and pan as the part
///     of the photo at the canvas centre (<see cref="ZoomGeometry.CentreOnImage"/>), so both survive a window resize
///     and a preview laid out at another size than the HQ they were saved on (a RAW's metadata size vs. its decoded
///     size, #195);</item>
///   <item>rotation is kept as the user's manual rotation only (total minus the image's EXIF baseline), so
///     re-applying it onto a different baseline — e.g. a pre-oriented preview thumbnail vs. an HQ image whose
///     EXIF rotation is applied at render — never double-counts the EXIF orientation.</item>
/// </list>
/// The zoom is saved at rest (no animation running), so it is also the target zoom.
/// <see cref="CanvasViewManager"/> decides <i>when</i> to save and applies the restored values to the live view.
/// </summary>
internal sealed class PerPhotoViewStore
{
    private readonly record struct PerPhotoViewState(float PhotoZoom, Point CentreOnImage, int UserRotation);

    private readonly Dictionary<string, PerPhotoViewState> _cache = [];

    /// <summary>A remembered view with its rotation re-based onto the current EXIF baseline.</summary>
    public readonly record struct RestoredView(float PhotoZoom, Point CentreOnImage, int Rotation);

    /// <summary>
    /// Saves the view for <paramref name="photoPath"/>, its rotation relative to <paramref name="exifRotation"/> so it
    /// survives baseline changes. The caller only calls this when the view is worth remembering.
    /// </summary>
    public void Save(string photoPath, float photoZoom, Point centreOnImage, int rotation, int exifRotation) =>
        _cache[photoPath] = new PerPhotoViewState(photoZoom, centreOnImage, rotation - exifRotation);

    /// <summary>
    /// If a view is remembered for <paramref name="photoPath"/>, returns it with its rotation re-based onto the
    /// <paramref name="exifRotation"/> baseline. That keeps a subsequent Preview → HQ rotation delta netting to zero
    /// even when the preview and HQ baselines differ.
    /// </summary>
    public bool TryRestore(string photoPath, int exifRotation, out RestoredView view)
    {
        if (!_cache.TryGetValue(photoPath, out var s))
        {
            view = default;
            return false;
        }
        view = new RestoredView(s.PhotoZoom, s.CentreOnImage, exifRotation + s.UserRotation);
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
