#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace FlyPhotos.Utils;

/// <summary>
/// Manages the visibility of a <see cref="UIElement"/>, fading it out after a period of user inactivity
/// and making it visible immediately upon reported activity. This class provides an auto-hide feature for UI controls.
/// </summary>
/// <remarks>
/// It uses asynchronous tasks with cancellation tokens for efficient delay management and a <see cref="Storyboard"/> for smooth animations.
/// </remarks>
/// <param name="element">The <see cref="UIElement"/> instance whose visibility and opacity will be controlled.</param>
/// <param name="hideDelayMs">The delay in milliseconds after user inactivity before the fade-out animation begins. Defaults to 500ms.</param>
/// <param name="fadeDurationMs">The duration of the fade-out animation in milliseconds. Defaults to 300ms.</param>
public partial class InactivityFader(UIElement element, int hideDelayMs = 500, int fadeDurationMs = 300) : IDisposable
{
    /// <summary>
    /// The <see cref="UIElement"/> instance whose visibility is being controlled.
    /// </summary>
    private readonly UIElement _element = element;

    /// <summary>
    /// The <see cref="TimeSpan"/> representing the delay after inactivity before the fade-out process starts.
    /// </summary>
    private readonly TimeSpan _hideDelay = TimeSpan.FromMilliseconds(hideDelayMs);

    /// <summary>
    /// The <see cref="Duration"/> of the fade-out animation.
    /// </summary>
    private readonly Duration _fadeDuration = new(TimeSpan.FromMilliseconds(fadeDurationMs));

    /// <summary>
    /// A <see cref="CancellationTokenSource"/> used to manage and cancel pending hide operations.
    /// </summary>
    private CancellationTokenSource? _cts;

    /// <summary>
    /// The <see cref="Storyboard"/> responsible for animating the element's opacity during fade-out.
    /// It is lazily initialized.
    /// </summary>
    private Storyboard? _fadeOutStoryboard;

    /// <summary>
    /// A flag indicating whether the <see cref="InactivityFader"/> instance has been disposed.
    /// </summary>
    private bool _isDisposed;

    /// <summary>
    /// Reports user activity. This immediately makes the controlled UI element visible
    /// and resets the timer for any pending fade-out operations.
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

    /// <summary>
    /// Asynchronously waits for the specified hide delay and then initiates the fade-out process.
    /// This operation can be cancelled if new activity is reported.
    /// </summary>
    /// <param name="token">A <see cref="CancellationToken"/> that can be used to cancel the delay.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous hide operation.</returns>
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

    /// <summary>
    /// Ensures the controlled UI element is visible and at full opacity (1.0).
    /// This method also stops any currently running fade-out animation.
    /// </summary>
    private void Show()
    {
        if (_isDisposed) return;

        // Stop any fade-out that might be in progress.
        _fadeOutStoryboard?.Stop();

        _element.Visibility = Visibility.Visible;
        _element.Opacity = 1.0;
    }

    /// <summary>
    /// Initiates a fade-out animation for the controlled UI element, gradually reducing its
    /// opacity to 0.0. Once the animation completes, the element's visibility is set to
    /// <see cref="Visibility.Collapsed"/> if its opacity remains 0.0.
    /// </summary>
    /// <remarks>
    /// This method ensures animation execution occurs safely on the UI thread and the <see cref="Storyboard"/>
    /// is lazily initialized.
    /// </remarks>
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
    /// Releases all resources used by the <see cref="InactivityFader"/> instance.
    /// This cancels any pending hide operations and disposes of the <see cref="CancellationTokenSource"/>.
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