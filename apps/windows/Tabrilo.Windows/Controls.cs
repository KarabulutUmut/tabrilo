using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Tabrilo.Windows;

internal static class Controls
{
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, MinHeight = 36 };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        button.Click += (_, _) => action();
        return button;
    }

    public static TextBlock Text(string text, double size = 14, bool secondary = false)
    {
        var element = new TextBlock { Text = text, FontSize = size * Theme.TextScale, TextWrapping = TextWrapping.Wrap };
        element.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "Secondary" : "Text");
        return element;
    }

    public static void Heading(Panel parent, string text, double size = 22)
    {
        var label = Text(text, size);
        label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 12, 0, 8);
        AutomationProperties.SetHeadingLevel(label, size >= 20 ? AutomationHeadingLevel.Level1 : AutomationHeadingLevel.Level2);
        parent.Children.Add(label);
    }

    public static ComboBox Choice(Panel parent, string label, string[] values, string initial)
    {
        var control = new ComboBox { ItemsSource = values, SelectedItem = initial, MinHeight = 30,
            Margin = new Thickness(0, 4, 0, 8) };
        AddLabel(parent, label, control);
        return control;
    }

    public static TextBox Field(Panel parent, string label, string value, int maxLength = 250)
    {
        var control = new TextBox { Text = value, MaxLength = maxLength };
        AddLabel(parent, label, control);
        return control;
    }

    public static void AddLabel(Panel parent, string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        parent.Children.Add(Text(label, 13));
        parent.Children.Add(control);
    }

    public static CheckBox Checkbox(Panel parent, string label, bool value)
    {
        var control = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 5, 0, 8) };
        control.SetResourceReference(Control.ForegroundProperty, "Text");
        parent.Children.Add(control);
        return control;
    }

    public static int Number(TextBox input, int min, int max, string label)
    {
        if (!int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value < min || value > max)
            throw new ArgumentException($"{label} must be between {min} and {max}.");
        return value;
    }

    public static BitmapSource Bitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static void CopyImage(byte[] png, BitmapSource image)
    {
        var data = new DataObject();
        using var pngData = new MemoryStream(png, writable: false);
        data.SetData("PNG", pngData);
        data.SetImage(image);
        Clipboard.SetDataObject(data, true);
    }

}
