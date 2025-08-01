using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Controls;

/// <summary>
/// A UserControl that defines the UI for the mini-player.
/// It is designed to be hosted within the <see cref="MiniPlayerWindow"/>.
/// </summary>
public sealed partial class MiniPlayerView : UserControl {
    /// <summary>
    /// Exposes the PlayerViewModel for data binding.
    /// </summary>
    public PlayerViewModel ViewModel { get; }

    public MiniPlayerView() {
        this.InitializeComponent();

        // Retrieve the singleton PlayerViewModel to ensure shared state.
        ViewModel = App.Services!.GetRequiredService<PlayerViewModel>();

        // Set the DataContext to this instance to enable {x:Bind} to the ViewModel property.
        this.DataContext = this;
    }

    /// <summary>
    /// Provides a reference to the Grid element that acts as the draggable region for the parent window.
    /// </summary>
    /// <returns>The Grid element designated as the draggable region.</returns>
    public Grid GetDraggableRegion() => DraggableRegion;
}