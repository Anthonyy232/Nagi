using System;
using System.Collections.Generic;
using System.Linq;
using Nagi.Core.Models;
using Xunit;

namespace Nagi.Core.Tests;

public class SelectionStateTests
{
    [Fact]
    public void Select_AddsToSelection()
    {
        var state = new SelectionState();
        var id = Guid.NewGuid();

        state.Select(id);

        Assert.True(state.IsSelected(id));
        Assert.Equal(1, state.GetSelectedCount(10));
    }

    [Fact]
    public void Deselect_RemovesFromSelection()
    {
        var state = new SelectionState();
        var id = Guid.NewGuid();

        state.Select(id);
        state.Deselect(id);

        Assert.False(state.IsSelected(id));
        Assert.Equal(0, state.GetSelectedCount(10));
    }

    [Fact]
    public void SelectAll_SetsSelectAllMode()
    {
        var state = new SelectionState();
        state.SelectAll();

        Assert.True(state.IsSelectAllMode);
        Assert.True(state.IsSelected(Guid.NewGuid()));
        Assert.Equal(10, state.GetSelectedCount(10));
    }

    [Fact]
    public void Deselect_InSelectAllMode_AddsToExceptions()
    {
        var state = new SelectionState();
        state.SelectAll();
        var id = Guid.NewGuid();

        state.Deselect(id);

        Assert.False(state.IsSelected(id));
        Assert.Equal(9, state.GetSelectedCount(10));
        Assert.Contains(id, state.GetExplicitlySelectedIds());
    }

    [Fact]
    public void Select_InSelectAllMode_RemovesFromExceptions()
    {
        var state = new SelectionState();
        state.SelectAll();
        var id = Guid.NewGuid();

        state.Deselect(id);
        Assert.False(state.IsSelected(id));

        state.Select(id);
        Assert.True(state.IsSelected(id));
        Assert.Empty(state.GetExplicitlySelectedIds());
    }

    [Fact]
    public void Clear_ResetsState()
    {
        var state = new SelectionState();
        state.Select(Guid.NewGuid());
        state.SelectAll();
        
        state.Clear();

        Assert.False(state.IsSelectAllMode);
        Assert.Equal(0, state.GetSelectedCount(10));
        Assert.Empty(state.GetExplicitlySelectedIds());
    }

    [Fact]
    public void GetSelectedIds_WorksCorrectly()
    {
        var state = new SelectionState();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var fullList = new List<Guid> { id1, id2, id3 };

        state.Select(id1);
        var selected = state.GetSelectedIds(fullList).ToList();
        Assert.Single(selected);
        Assert.Contains(id1, selected);

        state.SelectAll();
        state.Deselect(id2);
        selected = state.GetSelectedIds(fullList).ToList();
        Assert.Equal(2, selected.Count);
        Assert.Contains(id1, selected);
        Assert.Contains(id3, selected);
        Assert.DoesNotContain(id2, selected);
    }
}
