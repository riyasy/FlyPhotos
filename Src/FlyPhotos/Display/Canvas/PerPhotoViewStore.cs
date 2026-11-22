using System;
using System.Collections.Generic;
using Windows.Foundation;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Canvas;

/// <summary>
/// The views the user made, by photo path, for the RememberPerPhoto setting. Kept for the session only, in memory.
/// Stored in relative terms so they restore onto another window size or another image of the photo: the zoom on the
/// photo itself, the part of it at the centre (<see cref="ViewRules.CentreOnImage"/>), and the user's rotation without
/// the EXIF one. The scale on the image it was saved on is kept too: when that same image comes back, it is the exact
/// answer, whatever the photo's size by metadata says this time (a disk-cached preview reports the HQ's size, the
/// file's own preview the sensor's).
/// </summary>
internal sealed class PerPhotoViewStore
{
    /// <summary>A view as stored, relative to the photo rather than to one image of it.</summary>
    /// <param name="PhotoZoom">Screen pixels per photo pixel.</param>
    /// <param name="CentreOnImage">The part of the image at the canvas centre, as a 0–1 fraction of its box.</param>
    /// <param name="UserRotation">The user's turns on top of the EXIF rotation, in degrees.</param>
    /// <param name="ImageSize">The layout size of the image it was saved on; null when not given.</param>
    /// <param name="ImageScale">The scale on that image.</param>
    private readonly record struct Saved(float PhotoZoom, Point CentreOnImage, int UserRotation, Size ImageSize,
        float ImageScale);

    /// <summary>The stored views by photo path. Grows with every photo the user changes, until the window closes.</summary>
    private readonly Dictionary<string, Saved> _views = [];

    /// <summary>A remembered view, its rotation put back onto the image's EXIF rotation; the fields as in <see cref="Saved"/>.</summary>
    public readonly record struct RestoredView(float PhotoZoom, Point CentreOnImage, int Rotation, Size ImageSize,
        float ImageScale)
    {
        /// <summary>The scale to show an image of <paramref name="imageSize"/> at, if it is the image this view was saved on.</summary>
        public float? ScaleOn(Size imageSize) =>
            ImageSize != null && imageSize != null && Math.Abs(ImageSize.Width - imageSize.Width) < 0.5
            && Math.Abs(ImageSize.Height - imageSize.Height) < 0.5 ? ImageScale : null;
    }

    /// <summary>Stores the view of <paramref name="photoPath"/>, replacing any earlier one.</summary>
    /// <param name="photoPath">The photo the view belongs to.</param>
    /// <param name="photoZoom">Screen pixels per photo pixel.</param>
    /// <param name="centreOnImage">The part of the image at the canvas centre, as a 0–1 fraction of its box.</param>
    /// <param name="rotation">The view's whole rotation; <paramref name="exifRotation"/> is taken off it.</param>
    /// <param name="exifRotation">The EXIF rotation of the image on screen.</param>
    /// <param name="imageSize">The layout size of the image on screen, so the same image can get its exact scale back.</param>
    /// <param name="imageScale">The view's scale on that image.</param>
    public void Save(string photoPath, float photoZoom, Point centreOnImage, int rotation, int exifRotation,
        Size imageSize = null, float imageScale = 0f) =>
        _views[photoPath] = new Saved(photoZoom, centreOnImage, rotation - exifRotation, imageSize, imageScale);

    /// <summary>The stored view of <paramref name="photoPath"/>, its rotation on top of <paramref name="exifRotation"/>.</summary>
    public bool TryRestore(string photoPath, int exifRotation, out RestoredView view)
    {
        if (!_views.TryGetValue(photoPath, out var s))
        {
            view = default;
            return false;
        }
        view = new RestoredView(s.PhotoZoom, s.CentreOnImage, exifRotation + s.UserRotation, s.ImageSize, s.ImageScale);
        return true;
    }

    /// <summary>Forgets a photo's view (the user put it back to the default).</summary>
    public void Remove(string photoPath) => _views.Remove(photoPath);

    /// <summary>Moves a stored view to a renamed photo's new path.</summary>
    public void Rename(string oldPath, string newPath)
    {
        if (_views.Remove(oldPath, out var saved))
            _views[newPath] = saved;
    }
}
