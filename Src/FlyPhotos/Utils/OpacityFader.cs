using FlyPhotos.AppSettings;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using Windows.Foundation;
using FlyPhotos.NativeWrappers;

namespace FlyPhotos.Utils;

/// <summary>
/// Manages the opacity of a collection of UI elements, fading them in or out
/// based on the pointer's position relative to a designated tracking element.
/// It features a delayed initial fade-out at startup and responds dynamically
/// to pointer movements and application settings.
/// </summary>
public partial class OpacityFader : IDisposable
{
    /// <summary>
    /// A list of Composition Visuals corresponding to the UI elements whose opacity will be animated.
    /// </summary>
    private readonly List<Visual> _visuals;

    /// <summary>
    /// The Compositor instance used for creating and managing animations.
    /// </summary>
    private readonly Compositor _compositor;

    /// <summary>
    /// The default duration for opacity fade animations.
    /// </summary>
    private readonly TimeSpan _defaultDuration;

    /// <summary>
    /// The FrameworkElement that the OpacityFader monitors for pointer events
    /// to determine whether to fade elements.
    /// </summary>
    private readonly FrameworkElement _trackingElement;

    /// <summary>
    /// A flag indicating whether the managed UI elements are currently in a faded (lower opacity) state.
    /// </summary>
    private bool _isFaded;

    /// <summary>
    /// A DispatcherTimer used to introduce a delay before applying the initial fade state
    /// after the application starts or the tracking element loads.
    /// </summary>
    private readonly DispatcherTimer _initialFadeTimer;


    /// <summary>
    /// Initializes a new instance of the <see cref="OpacityFader"/> class.
    /// </summary>
    /// <param name="elementsToFade">
    /// An enumerable collection of <see cref="FrameworkElement"/> objects whose opacity will be animated.
    /// These elements are converted to Composition Visuals for animation.
    /// </param>
    /// <param name="trackingElement">
    /// The <see cref="FrameworkElement"/> to monitor for pointer movements. This element defines
    /// the coordinate space and boundaries for the "hot zone" that triggers fading.
    /// </param>
    /// <param name="fadeDuration">
    /// An optional <see cref="TimeSpan"/> specifying the duration of the opacity fade animation.
    /// If null, a default duration of 500 milliseconds is used.
    /// </param>
    public OpacityFader(IEnumerable<FrameworkElement> elementsToFade, FrameworkElement trackingElement, TimeSpan? fadeDuration = null)
    {
        ArgumentNullException.ThrowIfNull(elementsToFade);
        ArgumentNullException.ThrowIfNull(trackingElement);

        _visuals = [];
        foreach (var element in elementsToFade)
        {
            if (element != null)
                _visuals.Add(ElementCompositionPreview.GetElementVisual(element));
        }

        if (_visuals.Count == 0)
            throw new ArgumentException("No valid FrameworkElements provided to fade.");

        _compositor = _visuals[0].Compositor;
        _trackingElement = trackingElement;
        _defaultDuration = fadeDuration ?? TimeSpan.FromMilliseconds(500);
        _isFaded = false; // Initial state is assumed not faded, will be updated by InitializeFadeState

        _initialFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _initialFadeTimer.Tick += InitialFadeTimer_Tick;

        Attach();
        InitializeFadeState();
    }

    /// <summary>
    /// Attaches necessary event handlers to the tracking element.
    /// </summary>
    private void Attach()
    {
        _trackingElement.PointerMoved += TrackingElement_PointerMoved;
    }

    /// <summary>
    /// Detaches event handlers and stops the initial fade timer to clean up resources.
    /// This method is called during disposal.
    /// </summary>
    private void Detach()
    {
        if (_trackingElement != null)
        {
            _trackingElement.PointerMoved -= TrackingElement_PointerMoved;
            _trackingElement.Loaded -= TrackingElement_Loaded;
        }

        if (_initialFadeTimer != null)
        {
            _initialFadeTimer.Stop();
            _initialFadeTimer.Tick -= InitialFadeTimer_Tick;
        }
    }

    /// <summary>
    /// Initiates the process for determining and applying the initial fade state.
    /// This process is delayed if the tracking element is not yet loaded, or by a fixed
    /// duration if the element is already loaded. The fade only applies if auto-fade is enabled.
    /// </summary>
    private void InitializeFadeState()
    {
        if (!AppConfig.Settings.AutoFade)
            return; // If auto-fade is disabled, no initial fading is needed.

        if (!_trackingElement.IsLoaded)
            _trackingElement.Loaded += TrackingElement_Loaded;
        else
            _initialFadeTimer.Start();
    }

    /// <summary>
    /// Event handler for the <see cref="FrameworkElement.Loaded"/> event of the tracking element.
    /// Once the element is loaded, it unsubscribes itself and starts the initial fade timer.
    /// </summary>
    /// <param name="sender">The source of the event (the tracking element).</param>
    /// <param name="e">Event data.</param>
    private void TrackingElement_Loaded(object sender, RoutedEventArgs e)
    {
        _trackingElement.Loaded -= TrackingElement_Loaded;
        _initialFadeTimer.Start();
    }

    /// <summary>
    /// Event handler for the <see cref="DispatcherTimer.Tick"/> event of the initial fade timer.
    /// This method is invoked after the specified delay (2 seconds) and applies the initial fade logic.
    /// It also stops the timer as it's a one-shot operation.
    /// </summary>
    /// <param name="sender">The source of the event (the DispatcherTimer).</param>
    /// <param name="e">Event data.</param>
    private void InitialFadeTimer_Tick(object sender, object e)
    {
        _initialFadeTimer.Stop(); // Stop the timer after it ticks once

        var initialPointerPosRelativeToTrackingElement = new Point(0, -1); // Default to outside hot zone

        // Attempt to get the current cursor position if the tracking element has valid dimensions.
        if (_trackingElement.ActualWidth > 0 && _trackingElement.ActualHeight > 0)
        {
            if (Win32Methods.GetCursorPos(out var screenPoint))
            {
                var globalScreenPoint = new Point(screenPoint.X, screenPoint.Y);
                try
                {
                    // Transform the global screen coordinates to be relative to the tracking element.
                    var transform = _trackingElement.TransformToVisual(null); // 'null' represents the screen's coordinate system
                    initialPointerPosRelativeToTrackingElement = transform.Inverse.TransformPoint(globalScreenPoint);
                }
                catch (System.ArgumentException)
                {
                    // Log if the element's transform is not ready, keeping the default "outside" position.
                    System.Diagnostics.Debug.WriteLine("OpacityFader: _trackingElement not fully rendered for initial position calculation.");
                }
            }
        }
        ApplyFadeLogic(initialPointerPosRelativeToTrackingElement);
    }

    /// <summary>
    /// Event handler for the <see cref="UIElement.PointerMoved"/> event of the tracking element.
    /// This continuously updates the fade state based on the current pointer position.
    /// If the initial fade timer is still active, it is stopped immediately.
    /// </summary>
    /// <param name="sender">The source of the event (the tracking element).</param>
    /// <param name="e">Event data containing pointer information.</param>
    private void TrackingElement_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // If the initial fade timer is still running, stop it now that the user has interacted.
        if (_initialFadeTimer.IsEnabled)
            _initialFadeTimer.Stop();

        var pos = e.GetCurrentPoint(_trackingElement).Position;
        ApplyFadeLogic(pos);
    }

    /// <summary>
    /// Encapsulates the core logic for determining whether to fade elements in or out
    /// based on the pointer's position relative to the tracking element's "hot zone."
    /// </summary>
    /// <param name="pos">The current <see cref="Point"/> representing the pointer's
    /// position relative to the <see cref="_trackingElement"/>.</param>
    private void ApplyFadeLogic(Point pos)
    {
        // If auto-fade is disabled, ensure controls are fully visible and exit.
        if (!AppConfig.Settings.AutoFade)
        {
            if (_isFaded)
            {
                FadeTo(1.0f);
                _isFaded = false;
            }
            return;
        }

        var windowHeight = _trackingElement.ActualHeight;

        // If the tracking element has no height, cannot determine hot zone, so exit.
        if (windowHeight <= 0) return;

        // Defines the "hot zone" at the bottom of the window (30% or minimum 100px).
        var bottomThreshold = Math.Max(100, windowHeight * 0.30);
        var isPointerInHotZone = pos.Y >= windowHeight - bottomThreshold;

        // Debug output to monitor fader status.
        System.Diagnostics.Debug.WriteLine(
            $"FADER STATUS :: FADED : {_isFaded}, WindowHeight = {windowHeight}, Mouse Y Position = {pos.Y}, In Hot Zone: {isPointerInHotZone}");

        // Logic to fade in or out based on current state and pointer position.
        if (_isFaded && isPointerInHotZone)
        {
            // If currently faded and pointer entered the hot zone, fade controls in.
            FadeTo(1.0f);
            _isFaded = false;
        }
        else if (!_isFaded && !isPointerInHotZone)
        {
            // If not faded and pointer left the hot zone, fade controls out.
            var targetOpacity = (100 - AppConfig.Settings.FadeIntensity) / 100f;
            FadeTo(targetOpacity);
            _isFaded = true;
        }
    }


    /// <summary>
    /// Initiates a Composition animation to change the opacity of all managed UI elements
    /// to a specified target opacity.
    /// </summary>
    /// <param name="targetOpacity">The target opacity value (0.0 to 1.0) for the animation.</param>
    /// <param name="duration">
    /// An optional <see cref="TimeSpan"/> specifying the duration of the animation.
    /// If null, the <see cref="_defaultDuration"/> is used.
    /// </param>
    private void FadeTo(float targetOpacity, TimeSpan? duration = null)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1.0f, targetOpacity); // 1.0f indicates the end of the animation
        animation.Duration = duration ?? _defaultDuration;

        foreach (var visual in _visuals)
        {
            visual.StartAnimation("Opacity", animation);
        }
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or
    /// resetting unmanaged resources. This implementation calls <see cref="Detach"/>
    /// to unhook event handlers and suppresses finalization.
    /// </summary>
    public void Dispose()
    {
        Detach();
        GC.SuppressFinalize(this);
    }
}