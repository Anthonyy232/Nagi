using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

#pragma warning disable NS2002

namespace Nagi.Core.Tests;

public class SmartPlaylistServiceRandomTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly SmartPlaylistService _smartPlaylistService;

    public SmartPlaylistServiceRandomTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
        
        // Mock dependencies
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
    public async Task GetRandomSmartPlaylistIdAsync_WithEmptyDatabase_ReturnsNull()
    {
        var result = await _smartPlaylistService.GetRandomSmartPlaylistIdAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetRandomSmartPlaylistIdAsync_WithSingleItem_ReturnsThatItemId()
    {
        var playlist = new SmartPlaylist { Name = "Test Smart" };
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.SmartPlaylists.Add(playlist);
            await context.SaveChangesAsync();
        }

        var result = await _smartPlaylistService.GetRandomSmartPlaylistIdAsync();
        result.Should().Be(playlist.Id);
    }

    [Fact]
    public async Task GetSmartPlaylistCountAsync_ReturnsCorrectCount()
    {
        using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.SmartPlaylists.Add(new SmartPlaylist { Name = "SP1" });
            context.SmartPlaylists.Add(new SmartPlaylist { Name = "SP2" });
            await context.SaveChangesAsync();
        }

        var count = await _smartPlaylistService.GetSmartPlaylistCountAsync();
        count.Should().Be(2);
    }
}
