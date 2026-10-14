using System.Collections.Specialized;
using FluentAssertions;
using Nagi.Presentation.Helpers;
using Xunit;

namespace Nagi.Presentation.Tests;

public class ObservableRangeCollectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkUpdate_NotifiesBoundControlsOnceWithTheUpdatedItems(bool append)
    {
        var collection = new ObservableRangeCollection<int>([1, 2, 3]);
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => notifications.Add(e);

        collection.AppendOrReplace([20, 30], append);

        collection.Should().Equal(append ? [1, 2, 3, 20, 30] : [20, 30]);
        var notification = notifications.Should().ContainSingle().Subject;
        notification.Action.Should().Be(append ? NotifyCollectionChangedAction.Add : NotifyCollectionChangedAction.Reset);
        if (append)
        {
            notification.NewStartingIndex.Should().Be(3);
            notification.NewItems!.Cast<int>().Should().Equal(20, 30);
        }
    }



}
