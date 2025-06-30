// Nagi/Converters/Converters.cs

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Nagi.Converters;

/// <summary>
///     Converts a TimeSpan to a string in "m:ss" or "h:mm:ss" format.
/// </summary>
public class TimeSpanToTimeStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        TimeSpan timeSpan;

        if (value is TimeSpan ts)
            timeSpan = ts;
        else if (value is double seconds)
            // This is the key change: handle the double from the slider's value.
            // We create a TimeSpan from the total seconds.
            timeSpan = TimeSpan.FromSeconds(seconds);
        else
            // If the value is neither a TimeSpan nor a double, return a default string.
            return "0:00";

        // Format the TimeSpan into a "minutes:seconds" string.
        // The @"m\:ss" format correctly handles the colon as a literal character.
        return timeSpan.ToString(@"m\:ss");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        // This conversion is not needed for this scenario.
        throw new NotImplementedException();
    }
}

/// <summary>
///     Converts a boolean value to its inverse.
/// </summary>
public class BooleanToInverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return value;
    }
}

/// <summary>
///     Converts an ElementTheme enum to a user-friendly string representation.
/// </summary>
public class ElementThemeToFriendlyStringConverter : IValueConverter
{
    private static readonly Dictionary<ElementTheme, string> FriendlyNames = new()
    {
        { ElementTheme.Light, "Light" },
        { ElementTheme.Dark, "Dark" },
        { ElementTheme.Default, "Use system setting" }
    };

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is ElementTheme theme && FriendlyNames.TryGetValue(theme, out var name)) return name;
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is string strValue)
        {
            var pair = FriendlyNames.FirstOrDefault(kvp => kvp.Value == strValue);
            if (pair.Value != null) return pair.Key;
        }

        return DependencyProperty.UnsetValue;
    }
}

/// <summary>
///     Converts a boolean to a Visibility value (Visible/Collapsed).
/// </summary>
public class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>
    ///     If true, a true value converts to Collapsed and a false value to Visible.
    /// </summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var boolValue = value is bool b && b;
        if (Invert) boolValue = !boolValue;
        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var visibility = value is Visibility v && v == Visibility.Visible;
        return Invert ? !visibility : visibility;
    }
}

/// <summary>
///     Converts a string URI to a BitmapImage source, returning null for invalid URIs.
/// </summary>
public class StringToUriConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string uriString && !string.IsNullOrEmpty(uriString))
            try
            {
                return new BitmapImage(new Uri(uriString, UriKind.Absolute));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[StringToUriConverter] Failed to create BitmapImage from '{uriString}': {ex.Message}");
                return null;
            }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
///     Converts a string to a Visibility value. A non-empty string is Visible by default.
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    /// <summary>
    ///     If true, a non-empty string converts to Collapsed and an empty one to Visible.
    /// </summary>
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
///     Converts a collection to a Visibility value. Visible if the collection is not empty.
/// </summary>
public class CollectionToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is IEnumerable collection)
            return collection.Cast<object>().Any() ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
///     Converts a string to a Visibility value. If the string is null or empty,
///     it returns Collapsed; otherwise, it returns Visible. Can be inverted.
/// </summary>
public class NullOrEmptyStringToVisibilityConverter : IValueConverter
{
    /// <summary>
    ///     Gets or sets a value indicating whether to invert the logic.
    ///     If true, null/empty strings result in Visible, and non-empty strings result in Collapsed.
    /// </summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isNullOrEmpty = string.IsNullOrEmpty(value as string);

        if (Invert) return isNullOrEmpty ? Visibility.Visible : Visibility.Collapsed;
        return isNullOrEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}