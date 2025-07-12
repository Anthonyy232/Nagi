using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Nagi.Converters;

// Converts a TimeSpan or a double (representing seconds) to a formatted time string (e.g., "m:ss").
public class TimeSpanToTimeStringConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, string language) {
        TimeSpan timeSpan;

        if (value is TimeSpan ts) {
            timeSpan = ts;
        }
        // Handle double values, common for slider controls bound to playback position.
        else if (value is double seconds) {
            timeSpan = TimeSpan.FromSeconds(seconds);
        }
        else {
            return "0:00";
        }

        // The @"m\:ss" format correctly handles the colon as a literal character.
        return timeSpan.ToString(@"m\:ss");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

// Converts a boolean value to its inverse.
public class BooleanToInverseBooleanConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, string language) {
        if (value is bool b) return !b;
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        if (value is bool b) return !b;
        return value;
    }
}

// Converts an ElementTheme enum to a user-friendly string for display in UI.
public class ElementThemeToFriendlyStringConverter : IValueConverter {
    private static readonly Dictionary<ElementTheme, string> FriendlyNames = new()
    {
        { ElementTheme.Light, "Light" },
        { ElementTheme.Dark, "Dark" },
        { ElementTheme.Default, "Use system setting" }
    };

    public object Convert(object value, Type targetType, object parameter, string language) {
        if (value is ElementTheme theme && FriendlyNames.TryGetValue(theme, out var name)) return name;
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        if (value is string strValue) {
            var pair = FriendlyNames.FirstOrDefault(kvp => kvp.Value == strValue);
            if (pair.Value != null) return pair.Key;
        }

        return DependencyProperty.UnsetValue;
    }
}

// Converts a boolean to a Visibility value (Visible/Collapsed).
public class BooleanToVisibilityConverter : IValueConverter {
    // If true, a true value converts to Collapsed and a false value to Visible.
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language) {
        var boolValue = value is bool b && b;
        if (Invert) boolValue = !boolValue;
        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        var visibility = value is Visibility v && v == Visibility.Visible;
        return Invert ? !visibility : visibility;
    }
}

// Converts a string URI to a BitmapImage source, returning null for invalid or empty URIs.
public class StringToUriConverter : IValueConverter {
    public object? Convert(object value, Type targetType, object parameter, string language) {
        if (value is string uriString && !string.IsNullOrEmpty(uriString)) {
            try {
                return new BitmapImage(new Uri(uriString, UriKind.Absolute));
            }
            catch (Exception ex) {
                Debug.WriteLine($"[StringToUriConverter] Failed to create BitmapImage from '{uriString}': {ex.Message}");
                return null;
            }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

// Converts a string to a Visibility value. A non-empty string is Visible by default.
public class StringToVisibilityConverter : IValueConverter {
    // If true, a non-empty string converts to Collapsed and an empty one to Visible.
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language) {
        var isVisible = !string.IsNullOrWhiteSpace(value as string);

        if (Invert) isVisible = !isVisible;

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

// Converts a collection to a Visibility value. Visible if the collection is not null and not empty.
public class CollectionToVisibilityConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, string language) {
        if (value is IEnumerable collection) {
            return collection.Cast<object>().Any() ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

// Converts a string to Visibility. Collapsed if the string is null or empty, otherwise Visible.
public class NullOrEmptyStringToVisibilityConverter : IValueConverter {
    // If true, inverts the logic: Visible for null/empty, Collapsed for non-empty.
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language) {
        var isNullOrEmpty = string.IsNullOrEmpty(value as string);

        if (Invert) return isNullOrEmpty ? Visibility.Visible : Visibility.Collapsed;
        return isNullOrEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) {
        throw new NotImplementedException();
    }
}

// Converts an object to Visibility. Collapsed if the object is null, otherwise Visible.
public class ObjectToVisibilityConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, string language) {
        bool invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);

        if (value == null) {
            return invert ? Visibility.Visible : Visibility.Collapsed;
        }
        else {
            return invert ? Visibility.Collapsed : Visibility.Visible;
        }
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