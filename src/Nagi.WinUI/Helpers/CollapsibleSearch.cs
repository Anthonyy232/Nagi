using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Resources;
using Windows.System;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     A search box hidden behind a toggle button. Opening it focuses the box; closing it, or Escape,
///     clears the search. The button's tooltip and accessible name follow the state.
/// </summary>
internal sealed class CollapsibleSearch(
    Control stateOwner,
    Button toggleButton,
    TextBox searchBox,
    string searchToolTip,
    string expandedState,
    string collapsedState,
    Action clearSearch)
{
    public bool IsExpanded { get; private set; }

    public void Toggle()
    {
        if (IsExpanded)
            Collapse();
        else
            Expand();
    }

    public void HandleKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        Collapse();
        e.Handled = true;
    }

    /// <summary>
    ///     Shows the given state without moving focus or clearing the search, e.g. when a page loads.
    /// </summary>
    public void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
        var label = expanded ? Strings.Generic_CloseSearch : searchToolTip;
        ToolTipService.SetToolTip(toggleButton, label);
        AutomationProperties.SetName(toggleButton, label);
        VisualStateManager.GoToState(stateOwner, expanded ? expandedState : collapsedState, false);
    }

    private void Expand()
    {
        if (IsExpanded) return;
        SetExpanded(true);
        searchBox.Focus(FocusState.Programmatic);
    }

    private void Collapse()
    {
        if (!IsExpanded) return;
        SetExpanded(false);
        toggleButton.Focus(FocusState.Programmatic);
        clearSearch();
    }
}
