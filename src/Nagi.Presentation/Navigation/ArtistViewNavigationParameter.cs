using System;

namespace Nagi.Presentation.Navigation;

public sealed record ArtistViewNavigationParameter(Guid ArtistId) : INavigationParameter;
