using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace FlyPhotos.Controllers;

internal class ThemeController
{
    public static ThemeController Instance { get; } = new();
    public List<Window> Windows = [];
    private ElementTheme _currentTheme;

    private ThemeController()
    {
        _currentTheme = (ElementTheme)Enum.Parse(typeof(ElementTheme), App.Settings.Theme);
    }

    public void AddWindow(Window window)
    {
        window.Closed += Window_Closed;
        Windows.Add(window);
        if (window.Content is FrameworkElement rootElement) rootElement.RequestedTheme = _currentTheme;
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        var window = (Window)sender;
        if (window == null) return;
        window.Closed -= Window_Closed;
        RemoveWindow(window);
    }

    public void RemoveWindow(Window window)
    {
        Windows.Remove(window);
    }

    public void SetTheme(string theme)
    {
        _currentTheme = (ElementTheme)Enum.Parse(typeof(ElementTheme), theme);
        foreach (var window in Windows)
            if (window.Content is FrameworkElement rootElement)
                rootElement.RequestedTheme = _currentTheme;
    }

    public void SetBackGround(string backGround)
    {
        foreach (var window in Windows)
        {
            var backGroundChangeableWindow = window as IBackGroundChangeable;
            backGroundChangeableWindow?.SetWindowBackground(backGround);
        }
    }
}

public interface IBackGroundChangeable
{
    void SetWindowBackground(string backGround);
}