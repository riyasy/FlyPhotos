#nullable enable
using System;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ExifReading;
using FlyPhotos.Infra.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlyPhotos.UI.Controls;

public sealed partial class ExifPanel : UserControl
{
    private const double FieldFontSize = 12;

    public event Action? CloseRequested;

    private ExifData _data = ExifData.Empty;
    private bool _showAll;
    private int _loadToken;

    public ExifPanel()
    {
        InitializeComponent();
    }

    public async Task LoadAsync(string filePath)
    {
        var token = ++_loadToken;
        _showAll = false;
        ButtonToggleMode.Content = L.Get("Exif_ShowAll");
        var data = await ExifReader.ReadAsync(filePath);
        if (token != _loadToken) return; // superseded by a later LoadAsync call
        _data = data;
        Render();
    }

    private void ButtonClose_Click(object _, RoutedEventArgs _1) => CloseRequested?.Invoke();

    private void ButtonToggleMode_Click(object _, RoutedEventArgs _1)
    {
        _showAll = !_showAll;
        ButtonToggleMode.Content = L.Get(_showAll ? "Exif_ShowSummary" : "Exif_ShowAll");
        Render();
    }

    private void Render()
    {
        FieldsPanel.Children.Clear();

        if (_showAll)
        {
            foreach (var group in _data.All.Value)
            {
                FieldsPanel.Children.Add(CreateCategoryHeader(group.Category));
                foreach (var field in group.Fields) FieldsPanel.Children.Add(CreateFieldRow(field));
            }
        }
        else
        {
            foreach (var field in _data.Summary) FieldsPanel.Children.Add(CreateFieldRow(field));
        }
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

    private static Grid CreateFieldRow(ExifField field)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = field.Label,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = FieldFontSize
        };
        Grid.SetColumn(label, 0);

        FrameworkElement value = field.LinkUrl == null ? CreateValueText(field.Value) : CreateValueLink(field.Value, field.LinkUrl);
        Grid.SetColumn(value, 1);

        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
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
