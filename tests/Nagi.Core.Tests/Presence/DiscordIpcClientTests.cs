using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Presence;
using Xunit;

namespace Nagi.Core.Tests.Presence;

public sealed class DiscordIpcClientTests : IAsyncDisposable
{
    private static readonly DiscordActivity Playing = new()
    {
        Details = "Song",
        State = "by Artist",
        StatusDisplayType = DiscordStatusDisplay.Details,
        Timestamps = new DiscordTimestamps(1000, 181000),
        Assets = new DiscordAssets("logo", "Album", "play_icon", "Playing")
    };

    private static readonly DiscordActivity Paused = Playing with { State = "Paused", Timestamps = null };

    private readonly FakeDiscord _discord = new();
    private readonly DiscordIpcClient _client;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DiscordIpcClientTests()
    {
        _client = new DiscordIpcClient("123", NullLogger.Instance, () => ["nagi-test-missing-pipe", _discord.PipeName]);
        _client.Ready += () => _ready.TrySetResult();
    }

    [Fact]
    public async Task SetActivity_SendsTheFieldsDiscordExpects()
    {
        var handshake = await StartAsync();

        await _client.SetActivityAsync(Playing);

        handshake.GetRawText().Should().Be("""{"v":1,"client_id":"123"}""");
        (await _discord.ReadActivityAsync()).Should().Be(
            """{"state":"by Artist","details":"Song","timestamps":{"start":1000,"end":181000},"assets":{"large_image":"logo","large_text":"Album","small_image":"play_icon","small_text":"Playing"},"type":2,"status_display_type":2}""");
    }

    [Fact]
    public async Task SetActivity_SkipsAnActivityIdenticalToTheOneShown()
    {
        await StartAsync();

        await _client.SetActivityAsync(Playing);
        await _client.SetActivityAsync(Playing with { });
        await _client.SetActivityAsync(Paused);

        (await _discord.ReadActivityAsync()).Should().Contain("by Artist");
        (await _discord.ReadActivityAsync()).Should().Contain("Paused");
    }

    [Fact]
    public async Task AfterDiscordRestarts_TheSameActivityIsShownAgain()
    {
        await StartAsync();
        await _client.SetActivityAsync(Playing);
        await _discord.ReadActivityAsync();
        var readyAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Ready += () => readyAgain.TrySetResult();

        var reconnected = _discord.AcceptAsync();
        await _discord.DisconnectAsync();
        await reconnected;
        await readyAgain.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _client.SetActivityAsync(Playing);

        (await _discord.ReadActivityAsync()).Should().Contain("by Artist");
    }

    [Fact]
    public async Task Ping_IsAnsweredWithTheSamePayload()
    {
        await StartAsync();

        await _discord.SendAsync(3, """{"n":7}""");

        var (opcode, body) = await _discord.ReadAsync();
        opcode.Should().Be(4);
        body.GetRawText().Should().Be("""{"n":7}""");
    }

    [Fact]
    public async Task Dispose_ClosesThePipe()
    {
        await StartAsync();

        _client.Dispose();

        await _discord.WaitForCloseAsync();
    }

    private async Task<System.Text.Json.JsonElement> StartAsync()
    {
        var accepted = _discord.AcceptAsync();
        _client.Start();
        var handshake = await accepted;
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return handshake;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _discord.DisposeAsync();
    }
}
