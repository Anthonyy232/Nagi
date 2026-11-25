using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A dialog's content root. A dialog is as wide as its content asks for, so one holding a text box would resize
///     with the text typed into it; this asks for all the width the dialog offers.
/// </summary>
public sealed class FillWidthGrid : Grid
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = base.MeasureOverride(availableSize);
        return double.IsInfinity(availableSize.Width) ? desired : new Size(availableSize.Width, desired.Height);
    }
}
