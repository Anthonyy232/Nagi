using System;
using Nagi.Core.Models;

namespace Nagi.WinUI.Models;

public sealed record RecentListenEntry(RecentListen Listen, Song? Song = null)
{
    public string Title => Listen.Title;
    public string ArtistName => Listen.ArtistName;
    public string TimeText => Listen.TimestampUtc.ToLocalTime().ToString("t");
    public override string ToString() => $"{Title} — {ArtistName}";
}

public sealed record RecentListenDateHeader(DateTime Date, string Title)
{
    public override string ToString() => Title;
}
