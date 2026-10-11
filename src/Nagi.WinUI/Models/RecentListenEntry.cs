using System;
using Nagi.Core.Models;

namespace Nagi.WinUI.Models;

public sealed record RecentListenEntry(RecentListen Listen, string DateHeader)
{
    public string Title => Listen.Title;
    public string ArtistName => Listen.ArtistName;
    public string TimeText => Listen.TimestampUtc.ToLocalTime().ToString("t");
}
