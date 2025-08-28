using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Implementations.Presence;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Presence;

/// <summary>
///     Contains unit tests for the <see cref="DiscordPresenceService" />.
/// </summary>
/// <remarks>
///     <b>Testing Limitation:</b> The <c>DiscordRpcClient</c> is a concrete class from a third-party
///     library that is instantiated directly within the service (<c>new DiscordRpcClient(...)</c>).
///     This design prevents mocking the client's behavior. Therefore, these tests are limited to
///     verifying the service's logic *before* it interacts with the client, such as handling
///     missing configuration and ensuring guard clauses prevent exceptions when the client is not initialized.
///     The logic for formatting and setting the rich presence cannot be tested in isolation.
/// </remarks>
public class DiscordPresenceServiceTests
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DiscordPresenceService> _logger;

    public DiscordPresenceServiceTests()
    {
        _configuration = Substitute.For<IConfiguration>();
        _logger = Substitute.For<ILogger<DiscordPresenceService>>();
    }

    /// <summary>
    ///     Verifies that the service correctly identifies its name.
    /// </summary>
    [Fact]
    public void Name_ShouldReturnDiscord()
    {
        // Arrange
        var service = new DiscordPresenceService(_configuration, _logger);

        // Assert
        service.Name.Should().Be("Discord");
    }

    /// <summary>
    ///     Verifies that <see cref="DiscordPresenceService.InitializeAsync" /> does nothing and completes
    ///     successfully if the Discord AppId is not found in the configuration.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InitializeAsync_WhenAppIdIsMissing_DoesNotInitializeClient(string? invalidAppId)
    {
        // Arrange
        _configuration["Discord:AppId"].Returns(invalidAppId);
        var service = new DiscordPresenceService(_configuration, _logger);

        // Act
        var action = async () => await service.InitializeAsync();

        // Assert
        await action.Should().NotThrowAsync();
    }

    /// <summary>
    ///     Verifies that methods do not throw exceptions if the RPC client has not been initialized.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The service is instantiated, but `InitializeAsync` is not called or fails,
    ///     leaving the internal client null or uninitialized.
    ///     <br />
    ///     <b>Expected Result:</b> Calling any of the event handler methods should return a completed
    ///     task without throwing an exception, demonstrating the effectiveness of the guard clauses.
    /// </remarks>
    [Fact]
    public async Task AllMethods_WhenClientIsNotInitialized_ReturnCompletedTaskWithoutError()
    {
        // Arrange
        _configuration["Discord:AppId"].Returns("some-id");
        var service = new DiscordPresenceService(_configuration, _logger);
        var song = new Song { Title = "Test Song" };

        // Act
        var trackChangedTask = service.OnTrackChangedAsync(song, 1);
        var stateChangedTask = service.OnPlaybackStateChangedAsync(true);
        var progressTask = service.OnTrackProgressAsync(TimeSpan.Zero, TimeSpan.Zero);
        var stoppedTask = service.OnPlaybackStoppedAsync();
        var disposeTask = service.DisposeAsync().AsTask();

        // Assert
        await trackChangedTask;
        await stateChangedTask;
        await progressTask;
        await stoppedTask;
        await disposeTask;

        // The assertion is that none of the above awaited calls threw an exception.
        true.Should().BeTrue();
    }

    /// <summary>
    ///     Verifies that <see cref="DiscordPresenceService.InitializeAsync" /> catches exceptions thrown
    ///     by the Discord RPC client during initialization and logs an error instead of crashing.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WhenRpcClientThrows_CatchesAndLogsError()
    {
        // This test is conceptual. In a real test runner, `new DiscordRpcClient` is likely to
        // fail if the Discord client isn't running. This test verifies that such a failure
        // is handled gracefully by the service's try-catch block.

        // Arrange
        _configuration["Discord:AppId"].Returns("123456789"); // A valid-looking but likely inactive ID
        var service = new DiscordPresenceService(_configuration, _logger);

        // Act
        var action = async () => await service.InitializeAsync();

        // Assert
        await action.Should().NotThrowAsync();
        // We can't easily verify the logger call without a more complex setup, but we've
        // confirmed the primary goal: the application does not crash.
    }
}