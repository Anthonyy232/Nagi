using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services;
using Nagi.Presentation.Services.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Presentation.Tests;

public sealed class MusicServerPromptsTests : IDisposable
{
    private readonly IMusicServerService _musicServers = Substitute.For<IMusicServerService>();
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly IUIService _ui = Substitute.For<IUIService>();
    private readonly MusicServerPrompts _prompts;

    public MusicServerPromptsTests()
    {
        _prompts = new MusicServerPrompts(_musicServers, _playback, _ui, new ImmediateDispatcher(),
            NullLogger<MusicServerPrompts>.Instance);
        _prompts.Start();
    }

    public void Dispose() => _prompts.Dispose();

    [Fact]
    public async Task ServerSongFailed_SavedSignInRejected_OffersToSignInAgain()
    {
        var song = AddServerSong();
        _musicServers.CheckSongAsync(song).ThrowsAsync(new MusicServerException(MusicServerError.SignInFailed, "rejected"));
        _ui.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>())
            .Returns(true);

        _playback.ServerSongFailed += Raise.Event<Action<Song>>(song);

        await _ui.Received(1).ShowConfirmationDialogAsync(Arg.Any<string>(),
            string.Format(Strings.MusicServer_Error_SavedSignIn_Format, "Living Room"), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>());
        await _ui.Received(1).ShowMusicServerSignInDialogAsync(song.FolderId);
    }

    [Fact]
    public async Task ServerSongFailed_SongGoneFromTheServer_SaysSo()
    {
        var song = AddServerSong();
        _musicServers.CheckSongAsync(song).ThrowsAsync(new MusicServerException(MusicServerError.NotFound, "gone"));

        _playback.ServerSongFailed += Raise.Event<Action<Song>>(song);

        await _ui.Received(1).ShowMessageDialogAsync(Strings.Generic_Error,
            string.Format(Strings.Player_ServerSongGone_Format, "Breakwater", "Living Room"));
    }

    [Fact]
    public async Task ServerSongFailed_ServerLooksFine_SaysTheSongCouldntPlay()
    {
        var song = AddServerSong();

        _playback.ServerSongFailed += Raise.Event<Action<Song>>(song);

        await _ui.Received(1).ShowMessageDialogAsync(Strings.Generic_Error,
            string.Format(Strings.Player_ServerSongFailed_Format, "Breakwater", "Living Room"));
    }

    [Fact]
    public async Task ServerSongFailed_AgainWhileItsMessageIsOpen_ShowsOneMessage()
    {
        var song = AddServerSong();
        var messageOpen = new TaskCompletionSource();
        _ui.ShowMessageDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(messageOpen.Task, Task.CompletedTask);

        _playback.ServerSongFailed += Raise.Event<Action<Song>>(song);
        _playback.ServerSongFailed += Raise.Event<Action<Song>>(song);
        await _ui.ReceivedWithAnyArgs(1).ShowMessageDialogAsync(default!, default!);

        messageOpen.SetResult();
        _playback.ServerSongFailed += Raise.Event<Action<Song>>(song);
        await _ui.ReceivedWithAnyArgs(2).ShowMessageDialogAsync(default!, default!);
    }

    [Fact]
    public async Task SignInRejectedDuringASync_IsOfferedOncePerRun()
    {
        var folderId = AddServerSong().FolderId;

        _musicServers.SignInRejected += Raise.Event<Action<Guid>>(folderId);
        _musicServers.SignInRejected += Raise.Event<Action<Guid>>(folderId);

        await _ui.Received(1).ShowConfirmationDialogAsync(Arg.Any<string>(),
            string.Format(Strings.MusicServer_Error_SavedSignIn_Format, "Living Room"), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>());
    }

    private Song AddServerSong()
    {
        var folder = new Folder { Name = "Living Room", Path = "nagi-server:0123" };
        _musicServers.GetServerAsync(folder.Id).Returns(new MusicServer { FolderId = folder.Id, Folder = folder });
        return new Song { Title = "Breakwater", FolderId = folder.Id, RemoteId = "s1" };
    }
}
