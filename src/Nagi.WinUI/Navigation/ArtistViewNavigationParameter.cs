using System;

namespace Nagi.WinUI.Navigation;

public sealed record ArtistViewNavigationParameter(Guid ArtistId) : INavigationParameter;
