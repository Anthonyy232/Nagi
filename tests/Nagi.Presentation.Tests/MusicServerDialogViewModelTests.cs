using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests;

public class MusicServerDialogViewModelTests
{
    private readonly IMusicServerService _musicServers = Substitute.For<IMusicServerService>();
    private readonly MusicServerDialogViewModel _viewModel;

    public MusicServerDialogViewModelTests() =>
        _viewModel = new MusicServerDialogViewModel(_musicServers, NullLogger<MusicServerDialogViewModel>.Instance);

    [Fact]
    public async Task Submit_JellyfinAccountWithoutAPassword_AddsTheServer()
    {
        var folder = new Folder { Name = "Living Room" };
        _musicServers.AddAsync(MusicServerKind.Jellyfin, "jellyfin.local:8096", "alex", "").Returns(folder);
        _viewModel.Kind = MusicServerKind.Jellyfin;
        _viewModel.Address = "jellyfin.local:8096";
        _viewModel.UserName = "alex";

        _viewModel.CanSubmit.Should().BeTrue();
        (await _viewModel.SubmitAsync()).Should().BeTrue();
        _viewModel.AddedServer.Should().BeSameAs(folder);
    }

    [Theory]
    [InlineData(MusicServerKind.OpenSubsonic, false, MusicServerError.SignInFailed, nameof(Strings.MusicServer_Error_SignInFailed))]
    [InlineData(MusicServerKind.OpenSubsonic, true, MusicServerError.SignInFailed, nameof(Strings.MusicServer_Error_ApiKeyRejected))]
    [InlineData(MusicServerKind.OpenSubsonic, false, MusicServerError.SignInMethodUnsupported,
        nameof(Strings.MusicServer_Error_PasswordUnsupported))]
    [InlineData(MusicServerKind.OpenSubsonic, false, MusicServerError.UnsupportedServer, nameof(Strings.MusicServer_Error_NotOpenSubsonic))]
    [InlineData(MusicServerKind.Jellyfin, false, MusicServerError.UnsupportedServer, nameof(Strings.MusicServer_Error_NotJellyfin))]
    public async Task Submit_ServerRefuses_ExplainsWhyAndStaysOpen(MusicServerKind kind, bool signInWithApiKey,
        MusicServerError error, string expectedMessage)
    {
        var refusal = Task.FromException<Folder>(new MusicServerException(error, "refused"));
        _musicServers.AddAsync(default, default!, default!, default!).ReturnsForAnyArgs(refusal);
        _musicServers.AddWithApiKeyAsync(default!, default!).ReturnsForAnyArgs(refusal);
        _viewModel.Kind = kind;
        _viewModel.SignInWithApiKey = signInWithApiKey;
        _viewModel.Address = "music.example.com";
        _viewModel.UserName = "alex";
        _viewModel.ApiKey = "key";

        (await _viewModel.SubmitAsync()).Should().BeFalse();

        _viewModel.ErrorMessage.Should().Be(Strings.ResourceManager.GetString(expectedMessage));
    }

    [Fact]
    public async Task SignInAgain_ToAServerAddedWithAnApiKey_SendsTheNewKey()
    {
        var server = ApiKeyServer();
        _musicServers.GetServerAsync(server.FolderId).Returns(server);

        (await _viewModel.InitializeForSignInAsync(server.FolderId)).Should().BeTrue();
        _viewModel.ApiKey = "new-key";
        (await _viewModel.SubmitAsync()).Should().BeTrue();

        await _musicServers.Received(1).SignInAsync(server.FolderId, "new-key");
    }

    [Fact]
    public async Task SignInAgain_ProblemTheDialogCantFix_SaysWhatToDoInstead()
    {
        var server = ApiKeyServer();
        _musicServers.GetServerAsync(server.FolderId).Returns(server);
        _musicServers.SignInAsync(server.FolderId, "new-key")
            .Returns(Task.FromException(new MusicServerException(MusicServerError.SignInMethodUnsupported, "refused")));
        await _viewModel.InitializeForSignInAsync(server.FolderId);
        _viewModel.ApiKey = "new-key";

        (await _viewModel.SubmitAsync()).Should().BeFalse();

        _viewModel.ErrorMessage.Should().Be(string.Format(Strings.MusicServer_Error_SignInMethod_Format, "Navidrome"));
    }

    private static MusicServer ApiKeyServer() => new()
    {
        FolderId = Guid.NewGuid(),
        Kind = MusicServerKind.OpenSubsonic,
        BaseUrl = "https://music.example.com",
        UserName = "alex",
        UsesApiKey = true,
        Folder = new Folder { Name = "Navidrome" }
    };
}
