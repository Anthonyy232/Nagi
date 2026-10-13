using System.Collections.ObjectModel;
using System.Collections.Specialized;
using FluentAssertions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Models;
using Xunit;

namespace Nagi.WinUI.Tests;

public class IncrementalLoadingAdapterTests
{
    [Fact]
    public async Task LoadsPagesFromTheSourceAndReportsHowManyWereAdded()
    {
        var source = new PagedSource { PagesLeft = 1 };
        var adapter = new IncrementalLoadingAdapter(source);

        adapter.HasMoreItems.Should().BeTrue();
        (await adapter.LoadMoreItemsAsync(50)).Count.Should().Be(2u);

        adapter.HasMoreItems.Should().BeFalse();
        adapter.Count.Should().Be(2);
    }

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
        public int PagesLeft { get; set; }
        public bool HasMoreItems => PagesLeft > 0;

        public Task<int> LoadMoreItemsAsync()
        {
            PagesLeft--;
            Add(Count);
            Add(Count);
            return Task.FromResult(2);
        }
    }
}
