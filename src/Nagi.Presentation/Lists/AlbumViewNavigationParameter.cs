using Nagi.Presentation.Navigation;

namespace Nagi.Presentation.Lists;

public sealed record AlbumViewNavigationParameter(Guid AlbumId) : INavigationParameter;
