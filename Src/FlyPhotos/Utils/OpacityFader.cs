using System;
using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using FlyPhotos.AppSettings;

namespace FlyPhotos.Utils;

public class OpacityFader : IDisposable
{
    private readonly List<Visual> _visuals;
    private readonly Compositor _compositor;
    private readonly TimeSpan _defaultDuration;
    private readonly FrameworkElement _trackingElement;
    private bool _isFaded;

    /// <summary>
    /// Creates a fader that automatically adjusts the opacity of elements based on pointer position.
    /// </summary>
    /// <param name="elementsToFade">The UI elements whose opacity will be animated.</param>
    /// <param name="trackingElement">The UI element to monitor for pointer movements (e.g., the main layout grid).</param>
    /// <param name="fadeDuration">The duration of the fade animation.</param>
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
        _isFaded = false;

        Attach();
    }

    private void Attach()
    {
        _trackingElement.PointerMoved += TrackingElement_PointerMoved;
    }

    private void Detach()
    {
        if (_trackingElement != null)
        {
            _trackingElement.PointerMoved -= TrackingElement_PointerMoved;
        }
    }

    private void TrackingElement_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!AppConfig.Settings.AutoFade)
        {
            // If the setting is disabled and controls are currently faded, restore their opacity.
            if (_isFaded)
            {
                FadeTo(1.0f);
                _isFaded = false;
            }
            return;
        }

        var pos = e.GetCurrentPoint(_trackingElement).Position;
        double windowHeight = _trackingElement.ActualHeight;

        // The fade "hot zone" is the bottom 30% of the window, with a minimum of 100px.
        double bottomThreshold = Math.Max(100, windowHeight * 0.30);
        bool isPointerInHotZone = pos.Y >= windowHeight - bottomThreshold;

        if (_isFaded && isPointerInHotZone)
        {
            // Pointer entered the hot zone, fade controls in.
            FadeTo(1.0f);
            _isFaded = false;
        }
        else if (!_isFaded && !isPointerInHotZone)
        {
            // Pointer left the hot zone, fade controls out.
            float targetOpacity = (100 - AppConfig.Settings.FadeIntensity) / 100f;
            FadeTo(targetOpacity);
            _isFaded = true;
        }
    }

    private void FadeTo(float targetOpacity, TimeSpan? duration = null)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1.0f, targetOpacity);
        animation.Duration = duration ?? _defaultDuration;

        foreach (var visual in _visuals)
        {
            visual.StartAnimation("Opacity", animation);
        }
    }

    public void Dispose()
    {
        Detach();
        GC.SuppressFinalize(this);
    }
}