using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace Nagi.Presentation.Lists;

/// <summary>
///     A grid of albums, artists, genres, playlists or folders: what its page loads, searches, opens and plays.
/// </summary>
public interface ICollectionViewModel : INotifyPropertyChanged
{
    string SearchTerm { get; set; }

    /// <summary>Null for a collection that is neither searched nor sorted.</summary>
    ISortOrderSelection? Sort { get; }

    IAsyncRelayCommand PlayRandomCommand { get; }

    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Stops pending loads and searches when the page is left.</summary>
    void ResetState();

    /// <param name="item">One of the collection's items.</param>
    void Open(object item);
}
