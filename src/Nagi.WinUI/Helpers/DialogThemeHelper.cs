using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Nagi.WinUI.Helpers;

/// <summary>
/// Helper class to apply app-specific theme overrides to ContentDialogs.
/// ContentDialog renders in a separate popup layer and does not inherit ThemeDictionaries
/// from App.xaml, so we need to explicitly apply the overrides.
/// </summary>
public static class DialogThemeHelper
{
    // Fallback stroke color matching WinUI's default ControlStrokeColorDefault (dark theme)
    // This is used when we can't retrieve the theme resource directly
    private static readonly Color DefaultStrokeColor = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF);
    
    /// <summary>
    /// Applies the app's theme overrides to a ContentDialog to ensure consistent styling.
    /// This includes TextBox focused underline and accent button colors.
    /// This should be called before showing the dialog.
    /// </summary>
    public static void ApplyThemeOverrides(ContentDialog dialog)
    {
        if (dialog == null) return;
        
        // Get the app's primary color brush
        if (!Application.Current.Resources.TryGetValue("AppPrimaryColorBrush", out var accentBrushObj) ||
            accentBrushObj is not SolidColorBrush accentBrush)
        {
            return;
        }

        ApplyTextBoxOverrides(dialog, accentBrush);
        ApplyAccentButtonOverrides(dialog, accentBrush);
        ApplySelectionControlOverrides(dialog, accentBrush);
        ApplyProgressIndicatorOverrides(dialog, accentBrush);
    }
    
    /// <summary>
    /// Applies TextBox focused underline overrides using the gradient trick.
    /// </summary>
    private static void ApplyTextBoxOverrides(ContentDialog dialog, SolidColorBrush accentBrush)
    {
        // Create the gradient brush for the TextBox underline (dotMorten's trick)
        // The gradient creates a 2px underline at the bottom while keeping the rest transparent
        var gradientBrush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 2),
            RelativeTransform = new ScaleTransform { ScaleY = -1, CenterY = 0.5 }
        };
        
        // First two stops create the colored underline (bottom 2 pixels)
        gradientBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = accentBrush.Color });
        gradientBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = accentBrush.Color });
        
        // Third stop at offset 1.0 creates the transition to the normal border color
        // This is the "gradient trick" - both stops at 1.0 creates a sharp transition
        var strokeColor = GetControlStrokeColor();
        gradientBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = strokeColor });

        // Apply to dialog resources - these keys override the TextBox's focused border
        dialog.Resources["TextControlElevationBorderFocusedBrush"] = gradientBrush;
        dialog.Resources["TextControlBorderBrushFocused"] = gradientBrush;
        dialog.Resources["TextBoxBorderBrushFocused"] = accentBrush;
        
        // Apply text selection highlight color
        dialog.Resources["TextSelectionHighlightColor"] = accentBrush;
    }
    
    /// <summary>
    /// Applies accent button background color overrides to use the app's primary color.
    /// </summary>
    private static void ApplyAccentButtonOverrides(ContentDialog dialog, SolidColorBrush accentBrush)
    {
        // Create brushes for different button states
        // PointerOver and Pressed states use slightly modified opacity for visual feedback
        var normalBrush = new SolidColorBrush(accentBrush.Color);
        var pointerOverBrush = new SolidColorBrush(accentBrush.Color) { Opacity = 0.9 };
        var pressedBrush = new SolidColorBrush(accentBrush.Color) { Opacity = 0.8 };
        
        // Apply accent button background overrides
        // These keys are used by the built-in AccentButtonStyle
        dialog.Resources["AccentButtonBackground"] = normalBrush;
        dialog.Resources["AccentButtonBackgroundPointerOver"] = pointerOverBrush;
        dialog.Resources["AccentButtonBackgroundPressed"] = pressedBrush;
        
        // Also apply to the ContentDialog's own primary button (Save, Create, etc.)
        // These are the button-specific keys for ContentDialog
        dialog.Resources["ContentDialogButtonBackground"] = normalBrush;
        dialog.Resources["ContentDialogButtonBackgroundPointerOver"] = pointerOverBrush;
        dialog.Resources["ContentDialogButtonBackgroundPressed"] = pressedBrush;
        
        // HyperlinkButton foreground
        dialog.Resources["HyperlinkButtonForeground"] = accentBrush;
        dialog.Resources["HyperlinkButtonForegroundPointerOver"] = accentBrush;
        dialog.Resources["HyperlinkButtonForegroundPressed"] = accentBrush;
    }
    
    /// <summary>
    /// Applies accent colors to selection controls like ToggleSwitch, CheckBox, and RadioButton.
    /// </summary>
    private static void ApplySelectionControlOverrides(ContentDialog dialog, SolidColorBrush accentBrush)
    {
        // ToggleSwitch
        dialog.Resources["ToggleSwitchFillOn"] = accentBrush;
        dialog.Resources["ToggleSwitchFillOnPointerOver"] = accentBrush;
        dialog.Resources["ToggleSwitchFillOnPressed"] = accentBrush;
        
        // CheckBox
        dialog.Resources["CheckBoxFillOn"] = accentBrush;
        dialog.Resources["CheckBoxFillOnPointerOver"] = accentBrush;
        dialog.Resources["CheckBoxFillOnPressed"] = accentBrush;
        
        // RadioButton
        dialog.Resources["RadioButtonOuterEllipseCheckedFill"] = accentBrush;
        dialog.Resources["RadioButtonOuterEllipseCheckedFillPointerOver"] = accentBrush;
        dialog.Resources["RadioButtonOuterEllipseCheckedFillPressed"] = accentBrush;
        
        // ComboBox selection indicator
        dialog.Resources["ComboBoxItemPillFillBrush"] = accentBrush;
    }
    
    /// <summary>
    /// Applies accent colors to progress indicators.
    /// </summary>
    private static void ApplyProgressIndicatorOverrides(ContentDialog dialog, SolidColorBrush accentBrush)
    {
        dialog.Resources["ProgressBarForeground"] = accentBrush;
        dialog.Resources["ProgressBarIndeterminateForeground"] = accentBrush;
        dialog.Resources["ProgressRingForeground"] = accentBrush;
    }
    
    /// <summary>
    /// Attempts to get the ControlStrokeColorDefault from theme resources, with a fallback.
    /// </summary>
    private static Color GetControlStrokeColor()
    {
        // Try to get from Application resources first
        if (Application.Current.Resources.TryGetValue("ControlStrokeColorDefault", out var colorObj) &&
            colorObj is Color color)
        {
            return color;
        }
        
        // Fallback: use the default WinUI dark theme stroke color
        return DefaultStrokeColor;
    }
}
