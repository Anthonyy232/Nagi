using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Helpers;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Converters;

public sealed class PageSizeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int pageSize ? SettingsText.GetPageSizeName(pageSize) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        DependencyProperty.UnsetValue;
}

public sealed class AppIconToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is AppIcon icon ? AppIconGlyphs.ToGlyph(icon) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        DependencyProperty.UnsetValue;
}

/// <summary>
///     Converts a TimeSpan or a double (representing seconds) to a formatted time string (m:ss or h:mm:ss).
/// </summary>
public class TimeSpanToTimeStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var timeSpan = value switch
        {
            TimeSpan ts => ts,
            double seconds => TimeSpan.FromSeconds(seconds),
            _ => TimeSpan.Zero
        };

        return DurationFormatter.Format(timeSpan);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class BooleanToInverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return Convert(value, targetType, parameter, language);
    }
}

public class AppThemeToFriendlyStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is AppTheme theme ? SettingsText.GetName(theme) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        DependencyProperty.UnsetValue;
}

public class ArgbColorToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is ArgbColor color ? color.ToWindowsColor() : DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Color color ? color.ToArgbColor() : DependencyProperty.UnsetValue;
}

public class IncrementalLoadingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is IIncrementalItemSource source ? new IncrementalLoadingAdapter(source) : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>
    ///     If true, true becomes Collapsed and false becomes Visible.
    /// </summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isVisible = value is true;
        if (Invert) isVisible = !isVisible;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var isVisible = value is Visibility.Visible;
        return Invert ? !isVisible : isVisible;
    }
}

/// <summary>
///     Converts a string to Visibility. Visible if the string is not null or whitespace.
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isVisible = !string.IsNullOrWhiteSpace(value as string);
        if (Invert) isVisible = !isVisible;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
///     Converts a collection count (int) to Visibility. Visible if count > 0.
/// </summary>
public class CollectionCountToVisibilityConverter : IValueConverter
{
    /// <summary>
    ///     If true, 0 becomes Visible and >0 becomes Collapsed.
    /// </summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var count = value is int c ? c : 0;
        var isVisible = count > 0;
        if (Invert) isVisible = !isVisible;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
///     Converts a string to Visibility. Visible if the string is not null or empty.
/// </summary>
public class NullOrEmptyStringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isVisible = !string.IsNullOrEmpty(value as string);
        if (Invert) isVisible = !isVisible;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class ObjectToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isVisible = value != null;

        if ("Invert".Equals(parameter as string, StringComparison.OrdinalIgnoreCase)) isVisible = !isVisible;

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class GenreToGradientConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var (start, end) = GenreGradients.For(value as string);
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop { Color = start.ToWindowsColor(), Offset = 0.0 },
                new GradientStop { Color = end.ToWindowsColor(), Offset = 1.0 }
            }
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class BackdropMaterialToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is BackdropMaterial material ? SettingsText.GetName(material) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class PlayerBackgroundMaterialToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is PlayerBackgroundMaterial material ? SettingsText.GetName(material) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class BooleanToOpacityConverter : IValueConverter
{
    public double TrueValue { get; set; } = 1.0;
    public double FalseValue { get; set; } = 0.4;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? TrueValue : FalseValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
///     Safely converts a URI string to a BitmapImage. Returns null for empty or invalid strings.
/// </summary>
public class StringToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string language)
    {
        return Helpers.BitmapImageFactory.Create(value as string);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
///     Converts a URI string to a BitmapImage with an explicit logical decode width supplied
///     via ConverterParameter. Use this for surfaces (e.g. brushes) that can't set
///     DecodePixelWidth directly on the bitmap, when the source resolution is much larger
///     than the rendered area and you want to control memory + sharpness.
/// </summary>
public class StringToDecodedImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string language)
    {
        var decodePixelWidth = parameter switch
        {
            int i => i,
            double d => (int)d,
            string s when int.TryParse(s, out var n) => n,
            _ => 0,
        };
        return Helpers.BitmapImageFactory.CreateDecoded(value as string, decodePixelWidth);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
///     Converts a float or double to a formatted string with a fixed number of decimal places.
///     Default is 1 decimal place (e.g., "3.5" or "-12.0").
/// </summary>
public class FloatToDecimalStringConverter : IValueConverter
{
    public int DecimalPlaces { get; set; } = 1;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var format = $"F{DecimalPlaces}";
        return value switch
        {
            float f => f.ToString(format),
            double d => d.ToString(format),
            _ => value?.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
///     Compares an enum value to a string parameter and returns true if they match.
///     Used for RadioMenuFlyoutItem IsChecked bindings to indicate the selected sort order.
/// </summary>
public class SortOrderEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is null || parameter is not string paramString)
            return false;

        return string.Equals(value.ToString(), paramString, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
///     Supplies a positive maximum for sliders whose minimum is zero, avoiding invalid XAML ranges.
/// </summary>
public class SliderMaximumConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var maximum = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0.0
        };

        return maximum > 0 ? maximum : 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
///     Converts a double value (0.0 to 1.0) to a percentage string (e.g., "50%").
/// </summary>
public class DoubleToPercentageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            double d => d.ToString("P0"),
            float f => f.ToString("P0"),
            _ => 0d.ToString("P0")
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
///     Converts a double volume value (0-100) into a RectangleGeometry clip bound for a FontIcon.
///     Uses the ConverterParameter as the total width (e.g. "20").
/// </summary>
public class VolumeToClipRectConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double volume && parameter is string paramString && double.TryParse(paramString, out var width))
        {
            var ratio = Math.Clamp(volume / 100.0, 0.0, 1.0);
            return new RectangleGeometry
            {
                Rect = new Rect(0, 0, width * ratio, 100)
            };
        }

        return new RectangleGeometry { Rect = new Rect(0, 0, 0, 100) };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
