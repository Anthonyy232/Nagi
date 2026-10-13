using System.Collections.ObjectModel;
using System.Collections.Specialized;
using FluentAssertions;
using Nagi.Presentation.Models;
using Nagi.WinUI.Helpers;
using Xunit;

namespace Nagi.WinUI.Tests;

public class IncrementalLoadingAdapterTests
{
    [Fact]
    public void ListEditsReachTheSourceAndItsChangesAreRaisedByTheAdapter()
    {
        var source = new PagedSource { "a", "b", "c" };
        var adapter = new IncrementalLoadingAdapter(source);
        var raised = new List<(object? Sender, NotifyCollectionChangedAction Action)>();
        adapter.CollectionChanged += (sender, e) => raised.Add((sender, e.Action));

        adapter.RemoveAt(0);
        adapter.Insert(2, "a");

        source.Should().Equal("b", "c", "a");
        raised.Should().Equal((adapter, NotifyCollectionChangedAction.Remove), (adapter, NotifyCollectionChangedAction.Add));
    }

    private sealed class PagedSource : ObservableCollection<object>, IIncrementalItemSource
    {
        public bool HasMoreItems => false;
        public Task<int> LoadMoreItemsAsync() => Task.FromResult(0);
    }
}
