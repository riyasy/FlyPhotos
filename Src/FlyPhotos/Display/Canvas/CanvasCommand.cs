using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageRendering;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Canvas;

/// <summary>
/// Everything the UI thread can ask of the canvas, one record per request. <see cref="CanvasController"/> posts them;
/// <see cref="CanvasEngine"/> applies them in posting order at the top of the next W2D-thread Update, so a request
/// never races a frame that is being drawn. Points are canvas pixels (physical, not DIPs).
/// <para>
/// To add a request: add a record here, post it from <see cref="CanvasController"/>, and handle it in
/// <see cref="CanvasEngine"/>'s Apply switch.
/// </para>
/// </summary>
internal abstract record CanvasCommand
{
    // ---- What is shown ----

    /// <summary>
    /// Shows <paramref name="Renderer"/> for a photo and sets the view for it in the same tick.
    /// </summary>
    /// <param name="Photo">The photo, so the view can read its preview's metadata size (#195).</param>
    /// <param name="PhotoPath">The photo's path when the install was asked for; the view knows photos by path.</param>
    /// <param name="Item">
    /// The leased item the renderer draws. The engine owns the lease from here and ends it when the next install
    /// replaces this one. Null for an animator, which keeps its own frames.
    /// </param>
    /// <param name="Level">Placeholder, preview or HQ: decides how the install relates to what is on screen.</param>
    /// <param name="Renderer">Made on the UI thread by <see cref="RendererFactory"/>; the engine disposes it when replaced.</param>
    /// <param name="LayoutSize">The image's size in layout pixels (<see cref="ViewRules.LayoutSize"/>), taken on the UI thread.</param>
    /// <param name="Rotation">The image's EXIF rotation in degrees.</param>
    /// <param name="Checkered">The image can be transparent: draw the checkerboard under it when that setting is on.</param>
    /// <param name="FollowUp">
    /// The same image in a new renderer (an animator after its first frame): only the renderer changes; the view,
    /// the page and the strip stay.
    /// </param>
    public sealed record Install(Photo Photo, string PhotoPath, DisplayItem Item, DisplayLevel Level,
        IRenderer Renderer, Size LayoutSize, int Rotation, bool Checkered, bool FollowUp = false) : CanvasCommand;

    /// <summary>Next or previous page of a multi-page photo (TIFF, ICO, PDF).</summary>
    public sealed record ChangePage(NavDirection Direction) : CanvasCommand;

    /// <summary>
    /// A page load finished on the thread pool. <paramref name="Renderer"/> identifies which renderer loaded it, so a
    /// page that lands after the user moved on is ignored.
    /// </summary>
    /// <param name="Request">The engine's number for the request, so a failure of the newest one is told apart.</param>
    /// <param name="PageIndex">The page asked for. Carried, not read back: a newer load may already have moved the renderer on.</param>
    /// <param name="PageSize">The page's size; null when the load failed or a newer one superseded it.</param>
    public sealed record PageLoaded(IMultiPage Renderer, int Request, int PageIndex, Size PageSize) : CanvasCommand;

    /// <summary>A photo was renamed: the view and its remembered views follow the new path.</summary>
    public sealed record Renamed(string OldPath, string NewPath) : CanvasCommand;

    // ---- The view (ignored until a photo is shown) ----

    /// <summary>One wheel notch or zoom key, animated, around <paramref name="Anchor"/> (null: the canvas centre).</summary>
    public sealed record WheelZoom(ZoomDirection Direction, Point? Anchor) : CanvasCommand;

    /// <summary>
    /// Continuous zoom (touchpad, pinch, right-drag, Shift+wheel): scales by <paramref name="Factor"/> at once, around
    /// <paramref name="Anchor"/>. <paramref name="StopAtOneToOne"/>: a step that crosses 100% stops on it.
    /// </summary>
    public sealed record ZoomBy(float Factor, Point Anchor, bool StopAtOneToOne) : CanvasCommand;

    /// <summary>The next of the stops fit, 100% and 400%, animated; centred, or around <paramref name="Anchor"/>.</summary>
    public sealed record StepZoom(ZoomDirection Direction, Point? Anchor) : CanvasCommand;

    /// <summary>100%, animated; centred, or keeping <paramref name="Anchor"/> over the same image pixel.</summary>
    public sealed record ZoomToHundred(Point? Anchor) : CanvasCommand;

    /// <summary>Fits the image to the canvas, animated or at once.</summary>
    public sealed record Fit(bool Animate) : CanvasCommand;

    /// <summary>Moves the image by (<paramref name="Dx"/>, <paramref name="Dy"/>) canvas pixels.</summary>
    public sealed record Pan(double Dx, double Dy) : CanvasCommand;

    /// <summary>Turns the image by <paramref name="Degrees"/> (±90).</summary>
    public sealed record Rotate(int Degrees) : CanvasCommand;

    /// <summary>A short sideways shake: the user's action was refused.</summary>
    public sealed record Shrug : CanvasCommand;

    /// <summary>The closing animation: the image shrinks to a dot.</summary>
    public sealed record ExitZoom : CanvasCommand;

    // ---- The canvas ----

    /// <summary>
    /// The canvas changed size, in physical pixels; the view knows the size it had. With
    /// <paramref name="ImageSizedShift"/>, the window was resized around the image and the canvas origin moved by that
    /// much on screen, so the image is kept where it is.
    /// </summary>
    public sealed record Resized(Size NewSize, Point? ImageSizedShift = null) : CanvasCommand;

    /// <summary>The image scaling setting changed: renderers rebuild their scaled copies.</summary>
    public sealed record ScalingQualityChanged : CanvasCommand;

    /// <summary>Completes <paramref name="Done"/> when the next view animation arrives (not when one is cut short).</summary>
    public sealed record WaitForAnimation(TaskCompletionSource Done) : CanvasCommand;

    /// <summary>Frees the display items whose last lease ended (see <see cref="DisplayItem.FreeSink"/>).</summary>
    public sealed record FreeReleasedItems : CanvasCommand
    {
        /// <summary>Carries no data, so one instance serves every post.</summary>
        public static readonly FreeReleasedItems Instance = new();
    }
}
