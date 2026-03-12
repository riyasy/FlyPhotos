#nullable enable
using System;
using Windows.UI;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
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
        /// Lazy-initialized tint backdrop used for transparent window modes.
        /// </summary>
        private TransparentTintBackdrop TransparentTintBackdrop => field ??= new TransparentTintBackdrop
        {
            TintColor = Color.FromArgb((byte)(((100 - AppConfig.Settings.TransparentBackgroundIntensity) * 255) / 100), 0, 0, 0)
        };

        /// <summary>
        /// Lazy-initialized blurred backdrop used for frozen window modes.
        /// </summary>
        private BlurredBackdrop FrozenBackdrop => field ??= new BlurredBackdrop();

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
        /// Indicates if the window supports dynamic backdrop changes.
        /// </summary>
        private readonly bool _supportsBackDropChange;

        /// <summary>
        /// The default backdrop type to apply if none is specified or available.
        /// </summary>
        private const WindowBackdropType DefaultBackdrop = WindowBackdropType.Acrylic;

        /// <summary>
        /// Initializes a new instance of the <see cref="WindowAppearanceManager"/> class.
        /// </summary>
        /// <param name="window">The target window.</param>
        /// <param name="supportsBackDropChange">Whether changing the backdrop dynamically is supported.</param>
        public WindowAppearanceManager(Window window, bool supportsBackDropChange = true)
        {
            _window = window;
            _supportsBackDropChange = supportsBackDropChange;

            _configurationSource = new SystemBackdropConfiguration { IsInputActive = true };
            _window.Activated += Window_Activated;
            ((FrameworkElement)_window.Content).ActualThemeChanged += Window_ActualThemeChanged;
            SetConfigurationSourceTheme();
            SetWindowBackdropPrivate(supportsBackDropChange ? AppConfig.Settings.WindowBackdrop : DefaultBackdrop);
            SetWindowTheme(AppConfig.Settings.Theme);
        }

        /// <summary>
        /// Disposes of external resources and detaches event handlers.
        /// </summary>
        public void Dispose()
        {
            _window.Activated -= Window_Activated;
            //((FrameworkElement)_window.Content).ActualThemeChanged -= Window_ActualThemeChanged;
            _backdropController?.RemoveSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
            _backdropController?.Dispose();
            _backdropController = null;
        }

        /// <summary>
        /// Sets the window theme.
        /// </summary>
        /// <param name="theme">The element theme to apply.</param>
        public void SetWindowTheme(ElementTheme theme)
        {
            ((FrameworkElement)_window.Content).RequestedTheme = theme;
        }

        /// <summary>
        /// Sets the window backdrop if supported.
        /// </summary>
        /// <param name="backdropType">The type of backdrop to apply.</param>
        public void SetWindowBackdrop(WindowBackdropType backdropType)
        {
            if (_supportsBackDropChange) 
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
                    _window.SystemBackdrop = FrozenBackdrop;
                    break;

                case WindowBackdropType.None:
                default:
                    break;
            }
            SetBackColorAsPerThemeAndBackdrop();
        }

        /// <summary>
        /// Adjusts the transparency intensity of the transparent backdrop.
        /// </summary>
        /// <param name="transparencyIntensity">The intensity level (0-100).</param>
        public void SetWindowBackdropTransparency(int transparencyIntensity)
        {
            if (_currentBackdropType == WindowBackdropType.Transparent)
            {
                TransparentTintBackdrop.TintColor = Color.FromArgb((byte)(((100 - transparencyIntensity) * 255) / 100), 0, 0, 0);
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
            titleBar.ButtonForegroundColor = Colors.Gray;
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
            acrylicController.SetSystemBackdropConfiguration(_configurationSource);
            _backdropController = acrylicController;
        }

        /// <summary>
        /// Handles changes to the window's actual theme and updates the necessary configuration.
        /// </summary>
        private void Window_ActualThemeChanged(FrameworkElement sender, object args)
        {
            SetConfigurationSourceTheme();
            SetBackColorAsPerThemeAndBackdrop();
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
            _configurationSource.Theme = (SystemBackdropTheme)((FrameworkElement)_window.Content).ActualTheme;
        }

        /// <summary>
        /// Updates the root content background color depending on the applied theme and backdrop.
        /// </summary>
        private void SetBackColorAsPerThemeAndBackdrop()
        {
            var actualTheme = ((FrameworkElement)_window.Content).ActualTheme;
            Color gridColor;
            switch (_currentBackdropType)
            {
                case WindowBackdropType.None:
                    gridColor = actualTheme == ElementTheme.Light ? Colors.White : Colors.Black;
                    break;
                case WindowBackdropType.Frozen:
                    gridColor = actualTheme == ElementTheme.Light ? Colors.Transparent : ColorHelper.FromArgb(0x60, 0x00, 0x00, 0x00);
                    break;
                default:
                    gridColor = Colors.Transparent;
                    break;
            }
            ((Grid)_window.Content).Background = new SolidColorBrush(gridColor);
        }
    }

    /// <summary>
    /// Represents a customized brush backdrop that provides a blurred effect.
    /// </summary>
    public partial class BlurredBackdrop : CompositionBrushBackdrop
    {
        /// <summary>
        /// Creates the composition brush used by the backdrop.
        /// </summary>
        /// <param name="compositor">The associated compositor.</param>
        /// <returns>A host backdrop brush.</returns>
        protected override Windows.UI.Composition.CompositionBrush CreateBrush(Windows.UI.Composition.Compositor compositor)
        {
            return compositor.CreateHostBackdropBrush();
        }
    }
}
