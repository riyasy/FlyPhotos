#nullable enable
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace FlyPhotos.UI.Behaviors;

/// <summary>
/// Manages the visibility of a <see cref="UIElement"/>, fading it out after a period of user inactivity
/// and making it visible immediately upon reported activity.
/// </summary>
/// <remarks>
/// Must be created and used on the UI thread. The element's initial shown/hidden state is inferred from
/// its current <see cref="UIElement.Visibility"/> at construction time.
/// </remarks>
public sealed class InactivityFader : IDisposable
{
    private readonly UIElement _element;
    private readonly Duration _fadeDuration;
    private readonly DispatcherTimer _hideTimer;
    private Storyboard? _fadeOutStoryboard;
    private bool _isDisposed;
    private bool _isVisible;

    public InactivityFader(UIElement element, int hideDelayMs = 500, int fadeDurationMs = 300)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _fadeDuration = new Duration(TimeSpan.FromMilliseconds(fadeDurationMs));
        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(hideDelayMs)
        };
        _hideTimer.Tick += OnHideTimerTick;

        // Infer starting state from the element rather than imposing one,
        // so the first ReportActivity correctly Show()s a collapsed element.
        _isVisible = _element.Visibility == Visibility.Visible;
    }

    /// <summary>
    /// Reports user activity: ensures the element is visible and resets the hide timer.
    /// Must be called on the UI thread.
    /// </summary>
    public void ReportActivity()
    {
        if (_isDisposed)
            return;

        _hideTimer.Stop();
        if (!_isVisible)
            Show();
        _hideTimer.Start();
    }

    private void OnHideTimerTick(object? sender, object e)
    {
        _hideTimer.Stop();
        FadeOut();
    }

    private void Show()
    {
        _fadeOutStoryboard?.Stop();
        _element.Visibility = Visibility.Visible;
        _element.Opacity = 1.0;
        _isVisible = true;
    }

    private void FadeOut()
    {
        if (_isDisposed)
            return;

        // From this point the element is no longer in the stable-shown state:
        // incoming activity must re-Show() (which stops the storyboard and restores opacity).
        _isVisible = false;

        if (_fadeOutStoryboard == null)
        {
            var fadeOutAnimation = new DoubleAnimation
            {
                To = 0.0,
                Duration = _fadeDuration
            };
            Storyboard.SetTarget(fadeOutAnimation, _element);
            Storyboard.SetTargetProperty(fadeOutAnimation, nameof(UIElement.Opacity));
            _fadeOutStoryboard = new Storyboard();
            _fadeOutStoryboard.Children.Add(fadeOutAnimation);
            _fadeOutStoryboard.Completed += OnFadeOutCompleted;
        }

        _fadeOutStoryboard.Begin();
    }

    private void OnFadeOutCompleted(object? sender, object e)
    {
        if (_isDisposed)
            return;

        // Skip collapsing if activity interrupted the fade and re-showed the element.
        if (!_isVisible)
            _element.Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _hideTimer.Stop();
        _hideTimer.Tick -= OnHideTimerTick;
        if (_fadeOutStoryboard != null)
        {
            _fadeOutStoryboard.Completed -= OnFadeOutCompleted;
            _fadeOutStoryboard.Stop();
            _fadeOutStoryboard = null;
        }
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}