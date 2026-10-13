using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Models;

namespace Nagi.WinUI.Converters;

public sealed class PageSizeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is 0 ? Nagi.WinUI.Resources.Strings.PaginationControl_All : value?.ToString() ?? string.Empty;

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
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            AppTheme.Light => Nagi.WinUI.Resources.Strings.Theme_Light,
            AppTheme.Dark => Nagi.WinUI.Resources.Strings.Theme_Dark,
            AppTheme.Default => Nagi.WinUI.Resources.Strings.Theme_Default,
            _ => value?.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is string strValue)
        {
            foreach (var theme in Enum.GetValues<AppTheme>())
                if ((string)Convert(theme, targetType, parameter, language) == strValue) return theme;
        }

        return DependencyProperty.UnsetValue;
    }
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
    ///     Gets or sets a value indicating whether the conversion should be inverted.
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
    ///     Gets or sets a value indicating whether the conversion should be inverted.
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

/// <summary>
///     Converts a string value into a deterministic LinearGradientBrush from a curated palette.
/// </summary>
public class GenreToGradientConverter : IValueConverter
{
    private static readonly List<(Color Start, Color End)> Palettes = new()
    {
        (Color.FromArgb(255, 142, 45, 226), Color.FromArgb(255, 74, 0, 224)),
        (Color.FromArgb(255, 214, 109, 255), Color.FromArgb(255, 92, 3, 214)),
        (Color.FromArgb(255, 255, 95, 109), Color.FromArgb(255, 255, 195, 113)),
        (Color.FromArgb(255, 221, 94, 221), Color.FromArgb(255, 115, 54, 242)),
        (Color.FromArgb(255, 233, 30, 99), Color.FromArgb(255, 156, 39, 176)),
        (Color.FromArgb(255, 1, 200, 255), Color.FromArgb(255, 4, 105, 255)),
        (Color.FromArgb(255, 33, 150, 243), Color.FromArgb(255, 3, 169, 244)),
        (Color.FromArgb(255, 89, 92, 255), Color.FromArgb(255, 102, 194, 255)),
        (Color.FromArgb(255, 0, 212, 255), Color.FromArgb(255, 9, 9, 121)),
        (Color.FromArgb(255, 43, 88, 118), Color.FromArgb(255, 78, 67, 118)),
        (Color.FromArgb(255, 29, 212, 162), Color.FromArgb(255, 11, 171, 181)),
        (Color.FromArgb(255, 168, 255, 120), Color.FromArgb(255, 120, 255, 214)),
        (Color.FromArgb(255, 0, 150, 136), Color.FromArgb(255, 76, 175, 80)),
        (Color.FromArgb(255, 106, 17, 203), Color.FromArgb(255, 37, 117, 252)),
        (Color.FromArgb(255, 19, 84, 122), Color.FromArgb(255, 128, 208, 199)),
        (Color.FromArgb(255, 255, 107, 107), Color.FromArgb(255, 255, 159, 67)),
        (Color.FromArgb(255, 255, 193, 7), Color.FromArgb(255, 255, 87, 34)),
        (Color.FromArgb(255, 255, 110, 82), Color.FromArgb(255, 255, 183, 82)),
        (Color.FromArgb(255, 241, 39, 17), Color.FromArgb(255, 245, 175, 25)),
        (Color.FromArgb(255, 255, 204, 42), Color.FromArgb(255, 255, 153, 0)),
        (Color.FromArgb(255, 255, 0, 132), Color.FromArgb(255, 255, 100, 71)),
        (Color.FromArgb(255, 255, 225, 109), Color.FromArgb(255, 255, 109, 143)),
        (Color.FromArgb(255, 118, 75, 162), Color.FromArgb(255, 102, 126, 234)),
        (Color.FromArgb(255, 6, 214, 160), Color.FromArgb(255, 255, 209, 102))
    };

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string genreName || string.IsNullOrEmpty(genreName)) return GetDefaultGradient();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(genreName));
        var index = (int)(BitConverter.ToUInt32(hash, 0) % (uint)Palettes.Count);
        var (startColor, endColor) = Palettes[index];

        return CreateGradient(startColor, endColor);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }

    private static LinearGradientBrush CreateGradient(Color start, Color end)
    {
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop { Color = start, Offset = 0.0 },
                new GradientStop { Color = end, Offset = 1.0 }
            }
        };
    }

    private static LinearGradientBrush GetDefaultGradient()
    {
        return CreateGradient(Color.FromArgb(255, 88, 88, 88), Color.FromArgb(255, 58, 58, 58));
    }
}

public class BackdropMaterialToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is BackdropMaterial material)
            return material switch
            {
                BackdropMaterial.Mica => Nagi.WinUI.Resources.Strings.Backdrop_Mica,
                BackdropMaterial.MicaAlt => Nagi.WinUI.Resources.Strings.Backdrop_MicaAlt,
                BackdropMaterial.Acrylic => Nagi.WinUI.Resources.Strings.Backdrop_Acrylic,
                _ => material.ToString()
            };
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class PlayerBackgroundMaterialToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is PlayerBackgroundMaterial material)
            return material switch
            {
                PlayerBackgroundMaterial.Acrylic => Nagi.WinUI.Resources.Strings.PlayerBackgroundMaterial_Acrylic,
                PlayerBackgroundMaterial.Solid => Nagi.WinUI.Resources.Strings.PlayerBackgroundMaterial_Solid,
                _ => material.ToString()
            };
        return string.Empty;
    }

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
