using Nagi.Presentation.Navigation;

namespace Nagi.Presentation.Lists;

public sealed record ArtistViewNavigationParameter(Guid ArtistId) : INavigationParameter;
