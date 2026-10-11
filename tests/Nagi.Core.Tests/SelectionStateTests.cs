using System;
using System.Linq;
using Nagi.Core.Models;
using Xunit;

namespace Nagi.Core.Tests;

public class SelectionStateTests
{
    [Fact]
    public void ManualSelection_TracksMembershipCountAndIds_AndCanBeCleared()
    {
        var state = new SelectionState();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var fullList = new[] { id1, id2, Guid.NewGuid() };

        state.Select(id1);
        state.Select(id2);
        state.Deselect(id2);

        Assert.True(state.IsSelected(id1));
        Assert.False(state.IsSelected(id2));
        Assert.Equal(1, state.GetSelectedCount(fullList.Length));
        Assert.Equal(new[] { id1 }, state.GetSelectedIds(fullList).ToArray());

        state.DeselectAll();

        Assert.False(state.IsSelectAllMode);
        Assert.Equal(0, state.GetSelectedCount(fullList.Length));
        Assert.Empty(state.GetSelectedIds(fullList));
    }

    [Fact]
    public void SelectAll_TracksExceptionsAndReselection_AndCanBeCleared()
    {
        var state = new SelectionState();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var fullList = new[] { id1, id2, id3 };

        state.Select(id1);
        state.SelectAll();

        Assert.True(state.IsSelectAllMode);
        Assert.True(state.IsSelected(id1));
        Assert.Equal(fullList.Length, state.GetSelectedCount(fullList.Length));
        Assert.Equal(fullList, state.GetSelectedIds(fullList).ToArray());

        state.Deselect(id2);

        Assert.False(state.IsSelected(id2));
        Assert.Equal(2, state.GetSelectedCount(fullList.Length));
        Assert.Equal(new[] { id1, id3 }, state.GetSelectedIds(fullList).ToArray());

        state.Select(id2);

        Assert.True(state.IsSelected(id2));
        Assert.Equal(fullList.Length, state.GetSelectedCount(fullList.Length));
        Assert.Equal(fullList, state.GetSelectedIds(fullList).ToArray());

        state.DeselectAll();

        Assert.False(state.IsSelectAllMode);
        Assert.Equal(0, state.GetSelectedCount(fullList.Length));
        Assert.Empty(state.GetSelectedIds(fullList));
    }
}
