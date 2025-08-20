using System;
using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FlyPhotos.Utils;

public class OpacityFader
{
    private readonly List<Visual> _visuals;
    private readonly Compositor _compositor;
    private readonly TimeSpan _defaultDuration;

    public OpacityFader(IEnumerable<FrameworkElement> elements, TimeSpan? fadeDuration = null)
    {
        if (elements == null)
            throw new ArgumentNullException(nameof(elements));

        _visuals = [];
        foreach (var element in elements)
        {
            if (element != null)
                _visuals.Add(ElementCompositionPreview.GetElementVisual(element));
        }

        if (_visuals.Count == 0)
            throw new ArgumentException("No valid FrameworkElements provided.");

        _compositor = _visuals[0].Compositor;
        _defaultDuration = fadeDuration ?? TimeSpan.FromMilliseconds(500);
    }

    public void FadeTo(float targetOpacity, TimeSpan? duration = null)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1.0f, targetOpacity);
        animation.Duration = duration ?? _defaultDuration;

        foreach (var visual in _visuals)
        {
            visual.StartAnimation("Opacity", animation);
        }
    }
}

