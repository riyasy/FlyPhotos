#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace FlyPhotos.Utils;

/// <summary>
/// Manages the visibility of a UIElement, fading it out after a period of inactivity
/// and making it visible immediately upon reported activity.
/// </summary>
public class InactivityFader : IDisposable
{
    private readonly UIElement _element;
    private readonly TimeSpan _hideDelay;
    private readonly Duration _fadeDuration;

    private CancellationTokenSource? _cts;
    private Storyboard? _fadeOutStoryboard;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new instance of the InactivityFader class.
    /// </summary>
    /// <param name="element">The UIElement to control.</param>
    /// <param name="hideDelayMs">The delay in milliseconds before starting the fade-out.</param>
    /// <param name="fadeDurationMs">The duration of the fade-out animation in milliseconds.</param>
    public InactivityFader(UIElement element, int hideDelayMs = 500, int fadeDurationMs = 300)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _hideDelay = TimeSpan.FromMilliseconds(hideDelayMs);
        _fadeDuration = new Duration(TimeSpan.FromMilliseconds(fadeDurationMs));
    }

    /// <summary>
    /// Reports user activity, making the element visible and resetting the fade-out timer.
    /// </summary>
    public void ReportActivity()
    {
        if (_isDisposed) return;

        // Cancel any previous hide request and create a new one.
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        // Show the element immediately.
        Show();

        // Start a new timer to hide the element after the delay.
        _ = HideAfterDelayAsync(_cts.Token);
    }

    private async Task HideAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_hideDelay, token);
            FadeOut();
        }
        catch (TaskCanceledException)
        {
            // Expected when activity is reported again, so we do nothing.
        }
    }

    private void Show()
    {
        // Ensure this logic runs on the UI thread.
        //_element.DispatcherQueue.TryEnqueue(() =>
        //{
        if (_isDisposed) return;

        // Stop any fade-out that might be in progress.
        _fadeOutStoryboard?.Stop();

        _element.Visibility = Visibility.Visible;
        _element.Opacity = 1.0;
        //});
    }

    private void FadeOut()
    {
        // Ensure this logic runs on the UI thread.
        _element.DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed) return;

            // Lazily create the storyboard once.
            if (_fadeOutStoryboard == null)
            {
                var fadeOutAnimation = new DoubleAnimation
                {
                    To = 0.0,
                    Duration = _fadeDuration,
                    EnableDependentAnimation = true // Use with caution, but necessary for Opacity on some elements.
                };
                Storyboard.SetTarget(fadeOutAnimation, _element);
                Storyboard.SetTargetProperty(fadeOutAnimation, "Opacity");

                _fadeOutStoryboard = new Storyboard();
                _fadeOutStoryboard.Children.Add(fadeOutAnimation);
                _fadeOutStoryboard.Completed += (_, _) =>
                {
                    // Check if we are still supposed to be hidden. An activity report could have
                    // occurred between the animation start and completion.
                    if (_element.Opacity == 0.0)
                    {
                        _element.Visibility = Visibility.Collapsed;
                    }
                };
            }
            _fadeOutStoryboard.Begin();
        });
    }

    /// <summary>
    /// Cleans up resources, cancelling any pending operations.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}