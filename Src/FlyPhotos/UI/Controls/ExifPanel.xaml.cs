#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ExifReading;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.UI.Behaviors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FlyPhotos.UI.Controls;

public sealed partial class ExifPanel : UserControl
{
    private const double FieldFontSize = 12;

    private ExifData _data = ExifData.Empty;
    private bool _showAll;
    private int _loadToken;
    private string? _loadedFilePath;

    public ExifPanel()
    {
        InitializeComponent();
        RtlLayoutBehavior.ApplyFlowDirection(this, AppConfig.Settings.Language);

        var copyTooltip = L.Get("Exif_CopyTooltip");
        ToolTipService.SetToolTip(ButtonCopy, copyTooltip);
        AutomationProperties.SetName(ButtonCopy, copyTooltip);
    }

    public void Toggle(string filePath)
    {
        if (IsOpen) Close();
        else Open(filePath);
    }

    // Dismiss the panel when the app navigates to a different file, but stay open on a
    // same-file refresh (e.g. preview -> HQ upgrade). Full paths are unique, so they
    // distinguish real navigation from two same-named files in different folders.
    public void CloseIfFileChanged(string currentFilePath)
    {
        if (IsOpen && !IsShowing(currentFilePath)) Close();
    }

    private bool IsOpen => Visibility == Visibility.Visible;

    private bool IsShowing(string filePath) =>
        string.Equals(_loadedFilePath, filePath, StringComparison.OrdinalIgnoreCase);

    private void Open(string filePath)
    {
        Visibility = Visibility.Visible;
        _ = LoadAsync(filePath);
    }

    private void Close() => Visibility = Visibility.Collapsed;

    private async Task LoadAsync(string filePath)
    {
        var token = ++_loadToken;
        _loadedFilePath = filePath;
        _showAll = false;
        ButtonToggleMode.Content = L.Get("Exif_ShowAll");

        // Clear the previous photo's rows synchronously (before the await) so they don't
        // linger in the visible panel while the new file's metadata is read in the background.
        _data = ExifData.Empty;
        Render();

        var data = await ExifReader.ReadAsync(filePath);
        if (token != _loadToken) return; // superseded by a later LoadAsync call
        _data = data;
        Render();
    }

    private void ButtonClose_Click(object _, RoutedEventArgs _1) => Close();

    private void ButtonCopy_Click(object _, RoutedEventArgs _1)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(BuildClipboardText());
        Clipboard.SetContent(dataPackage);
    }

    private string BuildClipboardText()
    {
        var sb = new StringBuilder();

        if (_showAll)
        {
            foreach (var group in _data.All.Value)
            {
                sb.AppendLine(group.Category);
                foreach (var field in group.Fields) sb.AppendLine($"{field.Label}: {field.Value}");
                sb.AppendLine();
            }
        }
        else
        {
            foreach (var field in _data.Summary) sb.AppendLine($"{field.Label}: {field.Value}");
        }

        return sb.ToString().TrimEnd();
    }

    private void ButtonToggleMode_Click(object _, RoutedEventArgs _1)
    {
        _showAll = !_showAll;
        ButtonToggleMode.Content = L.Get(_showAll ? "Exif_ShowSummary" : "Exif_ShowAll");
        Render();
    }

    private void Render()
    {
        FieldsPanel.Children.Clear();
        FieldsPanel.RowDefinitions.Clear();

        if (_showAll)
        {
            foreach (var group in _data.All.Value)
            {
                AddHeaderRow(CreateCategoryHeader(group.Category));
                foreach (var field in group.Fields) AddFieldRow(field);
            }
        }
        else
        {
            foreach (var field in _data.Summary) AddFieldRow(field);
        }
    }

    private int NextRow()
    {
        var row = FieldsPanel.RowDefinitions.Count;
        FieldsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return row;
    }

    private void AddHeaderRow(FrameworkElement header)
    {
        var row = NextRow();
        Grid.SetRow(header, row);
        Grid.SetColumnSpan(header, 2);
        FieldsPanel.Children.Add(header);
    }

    private void AddFieldRow(ExifField field)
    {
        var row = NextRow();

        var label = new TextBlock
        {
            Text = field.Label,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 160,
            Opacity = 0.7,
            FontSize = FieldFontSize
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);

        FrameworkElement value = field.LinkUrl == null ? CreateValueText(field.Value) : CreateValueLink(field.Value, field.LinkUrl);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);

        FieldsPanel.Children.Add(label);
        FieldsPanel.Children.Add(value);
    }

    private static TextBlock CreateCategoryHeader(string category)
    {
        return new TextBlock
        {
            Text = category,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 2)
        };
    }

    private static TextBlock CreateValueText(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = FieldFontSize
        };
    }

    private static HyperlinkButton CreateValueLink(string text, Uri url)
    {
        return new HyperlinkButton
        {
            Content = text,
            NavigateUri = url,
            Padding = new Thickness(0),
            FontSize = FieldFontSize
        };
    }
}
