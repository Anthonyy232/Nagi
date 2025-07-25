using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.UI;

namespace Nagi.WinUI.Converters;

/// <summary>
/// Converts a TimeSpan or a double (representing seconds) to a formatted time string (m:ss).
/// </summary>
public class TimeSpanToTimeStringConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, string language) {
        TimeSpan timeSpan = value switch {
            TimeSpan ts => ts,
            double seconds => TimeSpan.FromSeconds(seconds),
            _ => TimeSpan.Zero
        };

        // The @"m\:ss" format string ensures the colon is treated as a literal character.
        return timeSpan.ToString(@"m\:ss");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a boolean value to its inverse.
/// </summary>
public class BooleanToInverseBooleanConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, string language) {
        if (value is bool b) {
            return !b;
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        if (value is bool b) {
            return !b;
        }
        return value;
    }
}

/// <summary>
/// Converts an ElementTheme enum to a user-friendly string for display.
/// </summary>
public class ElementThemeToFriendlyStringConverter : IValueConverter {
    private static readonly Dictionary<ElementTheme, string> FriendlyNames = new()
    {
        { ElementTheme.Light, "Light" },
        { ElementTheme.Dark, "Dark" },
        { ElementTheme.Default, "Use system setting" }
    };

    public object Convert(object value, Type targetType, object parameter, string language) {
        if (value is ElementTheme theme && FriendlyNames.TryGetValue(theme, out var name)) {
            return name;
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        if (value is string strValue) {
            // Find the theme corresponding to the friendly name.
            var pair = FriendlyNames.FirstOrDefault(kvp => kvp.Value == strValue);
            if (pair.Value != null) {
                return pair.Key;
            }
        }

        return DependencyProperty.UnsetValue;
    }
}

/// <summary>
/// Converts a boolean to a Visibility value.
/// </summary>
public class BooleanToVisibilityConverter : IValueConverter {
    /// <summary>
    /// Gets or sets a value indicating whether to invert the conversion (true becomes Collapsed, false becomes Visible).
    /// </summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language) {
        bool isVisible = value is true;
        if (Invert) {
            isVisible = !isVisible;
        }
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        bool isVisible = value is Visibility.Visible;
        return Invert ? !isVisible : isVisible;
    }
}

/// <summary>
/// Converts a string URI to a BitmapImage source. Returns null for invalid or empty URIs.
/// </summary>
public class StringToUriConverter : IValueConverter {
    public object? Convert(object value, Type targetType, object parameter, string language) {
        if (value is string uriString && !string.IsNullOrEmpty(uriString)) {
            try {
                return new BitmapImage(new Uri(uriString, UriKind.Absolute));
            }
            catch (Exception ex) {
                // Log the error for debugging purposes if the URI is malformed.
                Debug.WriteLine($"[ERROR] StringToUriConverter: Failed to create BitmapImage from '{uriString}'. {ex.Message}");
                return null;
            }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a string to a Visibility value. Visible if the string is not null or consists of only whitespace.
/// </summary>
public class StringToVisibilityConverter : IValueConverter {
    /// <summary>
    /// Gets or sets a value indicating whether to invert the conversion (non-empty becomes Collapsed, empty becomes Visible).
    /// </summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language) {
        bool isVisible = !string.IsNullOrWhiteSpace(value as string);
        if (Invert) {
            isVisible = !isVisible;
        }
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a collection to a Visibility value. Visible if the collection is not null and not empty.
/// </summary>
public class CollectionToVisibilityConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, string language) {
        if (value is IEnumerable collection) {
            // Use LINQ's Any() to efficiently check if the collection contains any items.
            return collection.Cast<object>().Any() ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a string to Visibility. Visible if the string is not null or empty.
/// </summary>
public class NullOrEmptyStringToVisibilityConverter : IValueConverter {
    /// <summary>
    /// Gets or sets a value indicating whether to invert the logic (Visible for null/empty, Collapsed for non-empty).
    /// </summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language) {
        bool isVisible = !string.IsNullOrEmpty(value as string);
        if (Invert) {
            isVisible = !isVisible;
        }
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts an object to Visibility. Collapsed if the object is null.
/// </summary>
public class ObjectToVisibilityConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, string language) {
        // The converter parameter can be used to invert the behavior directly from XAML by passing "Invert".
        bool invert = "Invert".Equals(parameter as string, StringComparison.OrdinalIgnoreCase);

        bool isVisible = value != null;

        if (invert) {
            isVisible = !isVisible;
        }

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts an object to a boolean, returning true if the object is not null, false otherwise.
/// </summary>
public class ObjectToBooleanConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, string language) {
        return value != null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a string value into a unique, deterministic LinearGradientBrush from a curated palette.
/// This ensures that the same string always produces the same visually appealing gradient.
/// </summary>
public class GenreToGradientConverter : IValueConverter {
    // A vibrant, modern palette of 24 curated gradient pairs.
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

    public object Convert(object value, Type targetType, object parameter, string language) {
        if (value is not string genreName || string.IsNullOrEmpty(genreName)) {
            return GetDefaultGradient();
        }

        // Use the string's hash code to deterministically select a color palette.
        int seed = genreName.GetHashCode();
        int index = Math.Abs(seed) % Palettes.Count;
        var (startColor, endColor) = Palettes[index];

        return CreateGradient(startColor, endColor);
    }

    private static LinearGradientBrush CreateGradient(Color start, Color end) {
        return new LinearGradientBrush {
            StartPoint = new(0, 0),
            EndPoint = new(1, 1),
            GradientStops = new GradientStopCollection
            {
                new() { Color = start, Offset = 0.0 },
                new() { Color = end, Offset = 1.0 }
            }
        };
    }

    private static LinearGradientBrush GetDefaultGradient() {
        // A neutral gray gradient for default or error cases.
        return CreateGradient(Color.FromArgb(255, 88, 88, 88), Color.FromArgb(255, 58, 58, 58));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a string font family name into a FontFamily object.
/// Returns a default system font if the input string is null or empty.
/// </summary>
public class StringToFontFamilyConverter : IValueConverter {
    // This is the default system font for symbols, which corresponds to the 
    // SymbolThemeFontFamily resource.
    private static readonly FontFamily DefaultSymbolFont = new("Segoe MDL2 Assets");

    public object Convert(object value, Type targetType, object parameter, string language) {
        if (value is string fontFamilyName && !string.IsNullOrEmpty(fontFamilyName)) {
            return new FontFamily(fontFamilyName);
        }

        // Return the default symbol font to ensure the binding always receives a 
        // valid FontFamily object, preventing potential cast exceptions.
        return DefaultSymbolFont;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}