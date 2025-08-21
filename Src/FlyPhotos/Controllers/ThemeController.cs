using System;
using System.Collections.Generic;
using FlyPhotos.Data;
using Microsoft.UI.Xaml;

namespace FlyPhotos.Controllers;

internal class ThemeController : IDisposable
{
    public static ThemeController Instance { get; } = new();
    private List<Window> Windows = [];

    private ThemeController()
    {

    }

    public void AddWindow(Window window)
    {
        window.Closed += Window_Closed;
        Windows.Add(window);
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        var window = (Window)sender;
        if (window == null) return;
        window.Closed -= Window_Closed;
        Windows.Remove(window);
    }

    public void SetTheme(ElementTheme theme)
    {
        foreach (var window in Windows)
        {
            var themeChangeableWindow = window as IThemeChangeable;
            themeChangeableWindow?.SetWindowTheme(theme);
        }

    }

    public void SetBackGround(WindowBackdropType backdropType)
    {
        foreach (var window in Windows)
        {
            var backGroundChangeableWindow = window as IBackGroundChangeable;
            backGroundChangeableWindow?.SetWindowBackground(backdropType);
        }
    }

    public void Dispose()
    {
        foreach (var window in Windows)
        {
            window.Closed -= Window_Closed;
        }
        Windows.Clear();
    }
}

public interface IBackGroundChangeable
{
    void SetWindowBackground(WindowBackdropType backdropType);
}

public interface IThemeChangeable
{
    void SetWindowTheme(ElementTheme theme);
}