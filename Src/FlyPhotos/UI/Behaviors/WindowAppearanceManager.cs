#nullable enable
using System;
using Windows.UI;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;          // CanvasComposite
using Microsoft.Graphics.Canvas.Effects;  // CompositeEffect, ColorSourceEffect
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;
using WinUIEx;

namespace FlyPhotos.UI.Behaviors
{
    /// <summary>
    /// Manages the appearance of a window, including its backdrop (e.g., Mica, Acrylic) and theme (e.g., Light, Dark).
    /// </summary>
    internal partial class WindowAppearanceManager : IDisposable
    {
        /// <summary>
        /// Lazy-initialized tint backdrop used for the <see cref="WindowBackdropType.Transparent"/> mode.
        /// This must be WinUIEx's <see cref="TransparentTintBackdrop"/> (not a plain colour backdrop):
        /// it does the actual window see-through plumbing on connect — <c>DwmExtendFrameIntoClientArea</c>
        /// plus a <c>WM_ERASEBKGND</c> handler. A plain <see cref="SolidColorBackdrop"/> only paints a
        /// translucent brush over an opaque window, so the desktop would not show through.
        /// </summary>
        private TransparentTintBackdrop TransparentTintBackdrop => field ??= new TransparentTintBackdrop
        {
            TintColor = TransparentTint(AppConfig.Settings.TransparentBackgroundIntensity)
        };

        /// <summary>
        /// The Frozen backdrop currently assigned to the window, if any. A fresh instance is created
        /// each time the Frozen backdrop is applied (see <see cref="ApplyFrozenBackdrop"/>): the
        /// effect brush does not survive being detached and re-attached, so reusing one instance
        /// across backdrop switches leaves the window transparent and can crash. The dark-theme
        /// darkening is composited into this brush and updated <em>in place</em> on theme change
        /// (never reassigned) — reassigning <c>_window.SystemBackdrop</c> from inside the theme-change
        /// handler crashes, and the CanvasAnimatedControl swapchain occludes the Grid background so
        /// the darkening can no longer live on the Grid either.
        /// </summary>
        private BlurredBackdrop? _frozenBackdrop;

        /// <summary>
        /// The solid-colour backdrop currently assigned for the <see cref="WindowBackdropType.None"/>
        /// mode, if any. The CanvasAnimatedControl swapchain composites over (and so occludes) the
        /// root Grid background, so a plain Grid colour no longer paints the window — the opaque
        /// theme colour must be supplied as a system backdrop instead. Its colour is updated
        /// <em>in place</em> on theme change (never reassigned, which would crash).
        /// </summary>
        private SolidColorBackdrop? _noneBackdrop;

        /// <summary>
        /// The currently applied window backdrop type.
        /// </summary>
        private WindowBackdropType _currentBackdropType;

        /// <summary>
        /// The system backdrop controller, managing Acrylic or Mica effects.
        /// </summary>
        private ISystemBackdropControllerWithTargets? _backdropController;

        /// <summary>
        /// Configuration source to provide the system with theme and input state information.
        /// </summary>
        private readonly SystemBackdropConfiguration _configurationSource;

        /// <summary>
        /// The target window to manage appearance for.
        /// </summary>
        private readonly Window _window;

        /// <summary>
        /// The window's root content element, cached to avoid repeatedly casting <see cref="Window.Content"/>.
        /// </summary>
        private readonly FrameworkElement _root;

        /// <summary>
        /// Initializes a new instance of the <see cref="WindowAppearanceManager"/> class.
        /// </summary>
        /// <param name="window">The target window.</param>
        /// <param name="initialBackdrop">The backdrop to apply immediately.</param>
        public WindowAppearanceManager(Window window, WindowBackdropType initialBackdrop)
        {
            _window = window;
            _root = (FrameworkElement)_window.Content;

            // The CanvasAnimatedControl swapchain occludes the root Grid, so it never paints the
            // window background. Keep it permanently transparent and supply every opaque colour via a
            // system backdrop brush instead — there is no longer any per-backdrop Grid colour.
            ((Grid)_root).Background = new SolidColorBrush(Colors.Transparent);

            _configurationSource = new SystemBackdropConfiguration { IsInputActive = true };

            // Set the theme BEFORE subscribing ActualThemeChanged and BEFORE applying the backdrop.
            // This prevents two problems:
            //   1. A spurious ActualThemeChanged firing inside SetWindowBackdropPrivate (or the old
            //      SetWindowTheme call after it) that would re-run SetConfigurationSourceTheme and
            //      double-call UpdateCaptionButtonForeground.
            //   2. Mica/Acrylic being attached with the wrong theme in _configurationSource before
            //      the requested theme is resolved, which can produce a transient wrong-theme frame
            //      in dark mode.
            // Note: whether RequestedTheme resolves synchronously to ActualTheme before the handler
            // is subscribed depends on whether the content is already in the visual tree. Confirm
            // via WPA/ETW on-device; if ActualTheme lags, the first ActualThemeChanged after
            // layout will still correct it, but the wrong-theme frame risk remains until then.
            _root.RequestedTheme = AppConfig.Settings.Theme;

            _window.Activated += Window_Activated;
            _root.ActualThemeChanged += Window_ActualThemeChanged;

            // SetConfigurationSourceTheme is NOT called here. It is deferred to TrySetMicaBackdrop /
            // TrySetAcrylicBackdrop immediately before SetSystemBackdropConfiguration, so the COM
            // activation (ThemeSettings.CreateForWindowId) is skipped entirely for
            // Transparent / Frozen / None backdrops that never consume _configurationSource.
            SetWindowBackdropPrivate(initialBackdrop);
        }

        /// <summary>
        /// Disposes of external resources and detaches event handlers.
        /// </summary>
        public void Dispose()
        {
            _window.Activated -= Window_Activated;
            _root.ActualThemeChanged -= Window_ActualThemeChanged;
            _backdropController?.RemoveSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
            _backdropController?.Dispose();
            _backdropController = null;

            // Release the brush-based backdrops (Transparent / Frozen / None) and their composition
            // brushes; only the Mica/Acrylic controllers are torn down above.
            _window.SystemBackdrop = null;
            _frozenBackdrop = null;
            _noneBackdrop = null;
        }

        /// <summary>
        /// Sets the window theme.
        /// </summary>
        /// <param name="theme">The element theme to apply.</param>
        public void SetWindowTheme(ElementTheme theme)
        {
            _root.RequestedTheme = theme;
        }

        /// <summary>
        /// Sets the window backdrop.
        /// </summary>
        /// <param name="backdropType">The type of backdrop to apply.</param>
        public void SetWindowBackdrop(WindowBackdropType backdropType)
        {
            SetWindowBackdropPrivate(backdropType);
        }

        /// <summary>
        /// Handles the underlying logic of setting a window backdrop.
        /// </summary>
        /// <param name="backdropType">The type of backdrop to apply.</param>
        private void SetWindowBackdropPrivate(WindowBackdropType backdropType)
        {
            _currentBackdropType = backdropType;
            _backdropController?.RemoveSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
            _backdropController = null;

            _window.SystemBackdrop = null;
            // The previous brush-based backdrops (if any) are now detached; drop them so theme
            // changes can't touch a dead brush. Fresh ones are created if (re)applied below.
            _frozenBackdrop = null;
            _noneBackdrop = null;

            switch (backdropType)
            {
                case WindowBackdropType.Transparent:
                    _window.SystemBackdrop = TransparentTintBackdrop;
                    break;

                case WindowBackdropType.Acrylic:
                    TrySetAcrylicBackdrop(false);
                    break;

                case WindowBackdropType.AcrylicThin:
                    TrySetAcrylicBackdrop(true);
                    break;

                case WindowBackdropType.Mica:
                    TrySetMicaBackdrop(false);
                    break;

                case WindowBackdropType.MicaAlt:
                    TrySetMicaBackdrop(true);
                    break;

                case WindowBackdropType.Frozen:
                    ApplyFrozenBackdrop();
                    break;

                case WindowBackdropType.None:
                    ApplyNoneBackdrop();
                    break;

                default:
                    break;
            }
            UpdateCaptionButtonForeground();
        }

        /// <summary>
        /// Creates a fresh Frozen backdrop tinted for the current theme and assigns it to the window.
        /// A new instance is used every time (the effect brush cannot be detached and re-attached);
        /// theme changes thereafter update the tint in place rather than reassigning the backdrop
        /// (see <see cref="Window_ActualThemeChanged"/>).
        /// </summary>
        private void ApplyFrozenBackdrop()
        {
            _frozenBackdrop = new BlurredBackdrop(FrozenTint(_root.ActualTheme));
            _window.SystemBackdrop = _frozenBackdrop;
        }

        /// <summary>
        /// The tint composited over the Frozen blur for a given theme: transparent in light,
        /// translucent black in dark.
        /// </summary>
        private static Color FrozenTint(ElementTheme theme) =>
            theme == ElementTheme.Light ? Colors.Transparent : Color.FromArgb(0x60, 0x00, 0x00, 0x00);

        /// <summary>
        /// Creates a fresh opaque solid-colour backdrop for the current theme and assigns it for the
        /// <see cref="WindowBackdropType.None"/> mode. A system backdrop is used (rather than the
        /// root Grid background) because the CanvasAnimatedControl swapchain occludes the Grid;
        /// theme changes thereafter update the colour in place (see <see cref="Window_ActualThemeChanged"/>).
        /// </summary>
        private void ApplyNoneBackdrop()
        {
            _noneBackdrop = new SolidColorBackdrop(NoneColor(_root.ActualTheme));
            _window.SystemBackdrop = _noneBackdrop;
        }

        /// <summary>
        /// The opaque background colour for the <see cref="WindowBackdropType.None"/> mode:
        /// white in light, black in dark.
        /// </summary>
        private static Color NoneColor(ElementTheme theme) =>
            theme == ElementTheme.Light ? Colors.White : Colors.Black;

        /// <summary>
        /// The semi-transparent black tint for a given transparency intensity (0-100): higher
        /// intensity means more transparent (lower alpha).
        /// </summary>
        private static Color TransparentTint(int intensity) =>
            Color.FromArgb((byte)(((100 - intensity) * 255) / 100), 0, 0, 0);

        /// <summary>
        /// Adjusts the transparency intensity of the transparent backdrop. WinUIEx's
        /// <see cref="TransparentTintBackdrop"/> builds its brush once in <c>CreateBrush</c>, so the
        /// backdrop must be re-assigned to pick up the new tint.
        /// </summary>
        /// <param name="transparencyIntensity">The intensity level (0-100).</param>
        public void SetWindowBackdropTransparency(int transparencyIntensity)
        {
            if (_currentBackdropType == WindowBackdropType.Transparent)
            {
                TransparentTintBackdrop.TintColor = TransparentTint(transparencyIntensity);
                _window.SystemBackdrop = TransparentTintBackdrop;
            }
        }

        /// <summary>
        /// Configures the window to extend content into the title bar and makes it transparent.
        /// </summary>
        /// <param name="appTitlebar">Optional title bar element to set for the window.</param>
        public void SetupTransparentTitleBar(UIElement? appTitlebar)
        {
            if (appTitlebar != null) _window.SetTitleBar(appTitlebar);
            var titleBar = _window.AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            // ButtonForegroundColor is already current from the UpdateCaptionButtonForeground()
            // call at the end of SetWindowBackdropPrivate in the constructor — no redundant write.
        }

        /// <summary>
        /// Updates the caption button glyph color to stay legible against the current backdrop and theme.
        /// The transparent backdrop shows arbitrary content underneath, so the glyphs are always white;
        /// for every other backdrop they follow the theme (white in dark, black in light).
        /// </summary>
        private void UpdateCaptionButtonForeground()
        {
            var isLight = _root.ActualTheme == ElementTheme.Light;
            var titleBar = _window.AppWindow.TitleBar;

            // Transparent shows arbitrary content underneath, so glyphs are always white; every other
            // backdrop follows the theme.
            titleBar.ButtonForegroundColor = _currentBackdropType == WindowBackdropType.Transparent
                ? Colors.White
                : isLight ? Colors.Black : Colors.White;
        }

        /// <summary>
        /// Attempts to apply the Mica backdrop effect to the window.
        /// </summary>
        /// <param name="useMicaAlt">Whether to use the alternate Mica style (BaseAlt).</param>
        private void TrySetMicaBackdrop(bool useMicaAlt)
        {
            if (!MicaController.IsSupported()) return;
            var micaController = new MicaController { Kind = useMicaAlt ? MicaKind.BaseAlt : MicaKind.Base };
            micaController.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
            // Populate _configurationSource here, not in the constructor: the COM activation
            // (ThemeSettings.CreateForWindowId) is only needed on paths that call
            // SetSystemBackdropConfiguration.
            SetConfigurationSourceTheme();
            micaController.SetSystemBackdropConfiguration(_configurationSource);
            _backdropController = micaController;
        }

        /// <summary>
        /// Attempts to apply the Acrylic backdrop effect to the window.
        /// </summary>
        /// <param name="useAcrylicThin">Whether to use the thin Acrylic style.</param>
        private void TrySetAcrylicBackdrop(bool useAcrylicThin)
        {
            if (!DesktopAcrylicController.IsSupported()) return;
            var acrylicController = new DesktopAcrylicController
            { Kind = useAcrylicThin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base };
            acrylicController.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
            // Populate _configurationSource here, not in the constructor: the COM activation
            // (ThemeSettings.CreateForWindowId) is only needed on paths that call
            // SetSystemBackdropConfiguration.
            SetConfigurationSourceTheme();
            acrylicController.SetSystemBackdropConfiguration(_configurationSource);
            _backdropController = acrylicController;
        }

        /// <summary>
        /// Handles changes to the window's actual theme and updates the necessary configuration.
        /// </summary>
        private void Window_ActualThemeChanged(FrameworkElement sender, object args)
        {
            SetConfigurationSourceTheme();
            // Update the brush colour in place; do NOT reassign _window.SystemBackdrop here — doing
            // so from within the theme-change handler crashes.
            var actualTheme = _root.ActualTheme;
            if (_currentBackdropType == WindowBackdropType.Frozen)
                _frozenBackdrop?.UpdateTint(FrozenTint(actualTheme));
            else if (_currentBackdropType == WindowBackdropType.None)
                _noneBackdrop?.UpdateColor(NoneColor(actualTheme));
            UpdateCaptionButtonForeground();
        }

        /// <summary>
        /// Handles the window activated event to update the input state configuration.
        /// </summary>
        private void Window_Activated(object sender, WindowActivatedEventArgs args)
        {
            _configurationSource.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        }

        /// <summary>
        /// Applies the current high contrast and element theme settings to the backdrop configuration.
        /// </summary>
        private void SetConfigurationSourceTheme()
        {
            _configurationSource.IsHighContrast = ThemeSettings.CreateForWindowId(_window.AppWindow.Id).HighContrast;
            // Map explicitly rather than casting: ElementTheme and SystemBackdropTheme share ordinal
            // values today, but that is a coincidence, not a contract.
            _configurationSource.Theme = _root.ActualTheme switch
            {
                ElementTheme.Light => SystemBackdropTheme.Light,
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                _ => SystemBackdropTheme.Default
            };
        }
    }

    /// <summary>
    /// A brush backdrop providing a blurred host-backdrop effect with a solid tint composited over
    /// the blur. The tint colour can be updated <em>in place</em> via <see cref="UpdateTint"/>
    /// without reassigning the window's backdrop, because reassigning <c>SystemBackdrop</c> during a
    /// theme change crashes. A fully transparent tint yields a plain blur (light Frozen).
    /// </summary>
    internal partial class BlurredBackdrop(Color tint) : CompositionBrushBackdrop
    {
        private Color _tint = tint;
        private Windows.UI.Composition.Compositor? _compositor;
        private Windows.UI.Composition.CompositionEffectBrush? _brush;

        /// <summary>
        /// Updates the tint composited over the blur, animating the existing brush's colour in
        /// place. Safe to call before the brush is created — the value is then applied on creation.
        /// </summary>
        /// <param name="tint">The new tint colour.</param>
        public void UpdateTint(Color tint)
        {
            _tint = tint;
            if (_compositor is null || _brush is null) return;

            var animation = _compositor.CreateExpressionAnimation("color");
            animation.SetColorParameter("color", _tint);
            _brush.StartAnimation("TintColor.Color", animation);
        }

        /// <summary>
        /// Creates the composition brush used by the backdrop: a host-backdrop blur with the tint
        /// composited over it. The tint is registered as an animatable property so it can be
        /// updated in place by <see cref="UpdateTint"/>.
        /// </summary>
        /// <param name="compositor">The associated compositor.</param>
        /// <returns>A host backdrop brush with a solid tint composited on top.</returns>
        protected override Windows.UI.Composition.CompositionBrush CreateBrush(Windows.UI.Composition.Compositor compositor)
        {
            var hostBackdrop = compositor.CreateHostBackdropBrush();

            // Solid colour composited over the blur, so the darkening is part of the backdrop brush
            // and no longer depends on the Grid / CanvasAnimatedControl layer.
            var effect = new CompositeEffect
            {
                Mode = CanvasComposite.SourceOver,
                Sources =
                {
                    new Windows.UI.Composition.CompositionEffectSourceParameter("Backdrop"), // destination (blur)
                    new ColorSourceEffect { Name = "TintColor", Color = _tint }              // source, on top
                }
            };

            var factory = compositor.CreateEffectFactory(effect, ["TintColor.Color"]);
            var brush = factory.CreateBrush();
            brush.SetSourceParameter("Backdrop", hostBackdrop);

            _compositor = compositor;
            _brush = brush;
            return brush;
        }
    }

    /// <summary>
    /// A brush backdrop that paints a plain opaque colour. Used for <see cref="WindowBackdropType.None"/>:
    /// the CanvasAnimatedControl swapchain occludes the root Grid background, so the window's solid
    /// colour is supplied as a system backdrop instead. The colour can be updated <em>in place</em>
    /// via <see cref="UpdateColor"/> (a <see cref="Windows.UI.Composition.CompositionColorBrush"/>
    /// updates live), so theme changes never reassign <c>SystemBackdrop</c>, which would crash.
    /// </summary>
    internal partial class SolidColorBackdrop(Color color) : CompositionBrushBackdrop
    {
        private Color _color = color;
        private Windows.UI.Composition.CompositionColorBrush? _brush;

        /// <summary>
        /// Updates the backdrop colour in place. Safe to call before the brush is created — the
        /// value is then applied on creation.
        /// </summary>
        /// <param name="color">The new background colour.</param>
        public void UpdateColor(Color color)
        {
            _color = color;
            _brush?.Color = color;
        }

        /// <summary>
        /// Creates the composition brush used by the backdrop: a single opaque colour brush.
        /// </summary>
        /// <param name="compositor">The associated compositor.</param>
        /// <returns>A solid colour brush.</returns>
        protected override Windows.UI.Composition.CompositionBrush CreateBrush(Windows.UI.Composition.Compositor compositor)
        {
            _brush = compositor.CreateColorBrush(_color);
            return _brush;
        }
    }
}