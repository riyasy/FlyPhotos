using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace FlyPhotos.UI.Behaviors;

/// <summary>
///     Shows the previous/next edge arrows while the pointer is within <see cref="EdgeZoneWidth"/>
///     of the left or right window edge (Ref #243). Only the arrow elements themselves are
///     hit-testable, so every other pointer event in the edge zone still reaches the canvas.
/// </summary>
/// <remarks>
///     While <see cref="Enabled"/> is <see langword="false"/> the arrows are collapsed and no pointer
///     handler is attached, so the feature costs nothing. The fade itself is the arrows' XAML
///     <c>OpacityTransition</c>; this class only flips <see cref="UIElement.Opacity"/>.
/// </remarks>
internal sealed class EdgeNavArrowFader
{
    /// <summary>
    ///     Width of the hover zone at each side edge, in logical pixels. The arrow's inner edge sits at
    ///     ~54 (12 margin + 2 border + 16 padding + 24 glyph); the rest is a small lead-in.
    /// </summary>
    private const double EdgeZoneWidth = 60;

    /// <summary>Top strip left to <see cref="WindowCaptionButtonFader"/>'s title bar zone.</summary>
    private const double TopExclusionHeight = 40;

    private readonly UIElement _rootElement;
    private readonly UIElement _prevArrow;
    private readonly UIElement _nextArrow;
    private readonly PointerEventHandler _onPointerMoved;
    private readonly PointerEventHandler _onPointerExited;

    // Last applied states, so pointer moves inside a zone don't re-set Opacity every time.
    private bool _prevShown;
    private bool _nextShown;

    /// <summary>
    ///     Gets or sets whether the arrows are active. Toggling attaches/detaches the pointer
    ///     handlers and shows/collapses both arrows.
    /// </summary>
    internal bool Enabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value)
            {
                // handledEventsToo: the pointer is often over the canvas or an arrow button,
                // either of which may mark the event handled before it bubbles up here.
                _rootElement.AddHandler(UIElement.PointerMovedEvent, _onPointerMoved, true);
                _rootElement.AddHandler(UIElement.PointerExitedEvent, _onPointerExited, true);
            }
            else
            {
                _rootElement.RemoveHandler(UIElement.PointerMovedEvent, _onPointerMoved);
                _rootElement.RemoveHandler(UIElement.PointerExitedEvent, _onPointerExited);
                Apply(false, false);
            }
            _prevArrow.Visibility = _nextArrow.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <param name="rootElement">Full-window element whose pointer moves are observed.</param>
    /// <param name="prevArrow">Left-edge arrow; should start with Opacity 0.</param>
    /// <param name="nextArrow">Right-edge arrow; should start with Opacity 0.</param>
    /// <param name="enabled">Initial value for <see cref="Enabled"/>.</param>
    public EdgeNavArrowFader(UIElement rootElement, UIElement prevArrow, UIElement nextArrow, bool enabled)
    {
        _rootElement = rootElement;
        _prevArrow = prevArrow;
        _nextArrow = nextArrow;
        _onPointerMoved = OnPointerMoved;
        _onPointerExited = (_, _) => Apply(false, false);
        _prevArrow.Visibility = _nextArrow.Visibility = Visibility.Collapsed;
        Enabled = enabled;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(_rootElement).Position;
        var width = _rootElement.ActualSize.X;
        var inBand = pos.Y > TopExclusionHeight;
        Apply(inBand && pos.X <= EdgeZoneWidth, inBand && pos.X >= width - EdgeZoneWidth);
    }

    private void Apply(bool showPrev, bool showNext)
    {
        if (showPrev != _prevShown)
        {
            _prevShown = showPrev;
            _prevArrow.Opacity = showPrev ? 1 : 0;
        }
        if (showNext != _nextShown)
        {
            _nextShown = showNext;
            _nextArrow.Opacity = showNext ? 1 : 0;
        }
    }
}
