using FluentAssertions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Xunit;

namespace Nagi.Presentation.Tests;

public class GenreGradientsTests
{
    [Fact]
    public void For_PicksTheSameGradientInEveryRun()
    {
        // Pinned, so a per-process hash such as string.GetHashCode can't slip in and recolor genres on each launch.
        GenreGradients.For("Rock").Should().Be((ArgbColor.FromRgb(168, 255, 120), ArgbColor.FromRgb(120, 255, 214)));
    }
}
