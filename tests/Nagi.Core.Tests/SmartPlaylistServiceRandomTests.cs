using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class SmartPlaylistServiceRandomTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly SmartPlaylistService _smartPlaylistService;

    public SmartPlaylistServiceRandomTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();

        var fileSystem = Substitute.For<IFileSystemService>();
        var pathConfig = Substitute.For<IPathConfiguration>();
        var imageProcessor = Substitute.For<IImageProcessor>();
        var logger = Substitute.For<ILogger<SmartPlaylistService>>();

        _smartPlaylistService = new SmartPlaylistService(
            _dbHelper.ContextFactory,
            fileSystem,
            pathConfig,
            imageProcessor,
            logger);
    }

    public void Dispose()
    {
        _dbHelper.Dispose();
    }

    [Fact]
    public async Task GetRandomSmartPlaylistIdAsync_ReturnsNullWhenEmptyElseAStoredId()
    {
        (await _smartPlaylistService.GetRandomSmartPlaylistIdAsync()).Should().BeNull();

        var playlist = new SmartPlaylist { Name = "Test Smart" };
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.SmartPlaylists.Add(playlist);
            await context.SaveChangesAsync();
        }

        var result = await _smartPlaylistService.GetRandomSmartPlaylistIdAsync();
        result.Should().Be(playlist.Id);
    }

}
