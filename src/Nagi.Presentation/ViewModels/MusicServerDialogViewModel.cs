using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.MusicServers;
using Nagi.Presentation.Common;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Adds a music server, or signs in to one again once it stops accepting the saved sign-in. The server checks
///     the details before the dialog closes, so a mistake can be fixed in place.
/// </summary>
public sealed partial class MusicServerDialogViewModel : ObservableObject
{
    private readonly IMusicServerService _musicServers;
    private readonly ILogger<MusicServerDialogViewModel> _logger;
    private MusicServer? _signInServer;

    public MusicServerDialogViewModel(IMusicServerService musicServers, ILogger<MusicServerDialogViewModel> logger)
    {
        _musicServers = musicServers;
        _logger = logger;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindIndex), nameof(CanChooseSignInMethod), nameof(UsesApiKey),
        nameof(AsksForUserName), nameof(CanSubmit))]
    public partial MusicServerKind Kind { get; set; }

    /// <summary>
    ///     <see cref="Kind" /> as the dialog's "OpenSubsonic"/"Jellyfin" picker index.
    /// </summary>
    public int KindIndex
    {
        get => Kind == MusicServerKind.OpenSubsonic ? 0 : 1;
        set => Kind = value == 1 ? MusicServerKind.Jellyfin : MusicServerKind.OpenSubsonic;
    }

    /// <summary>
    ///     Whether the user chose to sign in to an OpenSubsonic server with an API key instead of a password.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignInMethodIndex), nameof(UsesApiKey), nameof(AsksForUserName), nameof(CanSubmit))]
    public partial bool SignInWithApiKey { get; set; }

    /// <summary>
    ///     <see cref="SignInWithApiKey" /> as the dialog's "Password"/"API Key" picker index.
    /// </summary>
    public int SignInMethodIndex
    {
        get => SignInWithApiKey ? 1 : 0;
        set => SignInWithApiKey = value == 1;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    public partial string Address { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    public partial string UserName { get; set; } = string.Empty;

    // Jellyfin accounts can have no password, so an empty one is allowed.
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    public partial bool IsBusy { get; set; }

    public bool IsAdding => _signInServer is null;
    public bool CanChooseSignInMethod => IsAdding && _musicServers.SupportsApiKey(Kind);
    public bool UsesApiKey => SignInWithApiKey && _musicServers.SupportsApiKey(Kind);
    public bool AsksForUserName => IsAdding && !UsesApiKey;
    public bool HasError => ErrorMessage is not null;

    public bool CanSubmit => !IsBusy
                             && (!IsAdding || !string.IsNullOrWhiteSpace(Address))
                             && (UsesApiKey ? !string.IsNullOrWhiteSpace(ApiKey) : !AsksForUserName || !string.IsNullOrWhiteSpace(UserName));

    public string Title => _signInServer is { } server
        ? ResourceFormatter.Format(Strings.MusicServerDialog_SignInTitle_Format, server.Folder.Name)
        : Strings.MusicServerDialog_AddTitle;

    public string PrimaryButtonText => IsAdding ? Strings.Generic_Add : Strings.MusicServerDialog_SignInButton;

    public string? SignInDescription => _signInServer switch
    {
        null => null,
        { UsesApiKey: true } server => ResourceFormatter.Format(Strings.MusicServerDialog_SignInApiKey_Format, server.BaseUrl),
        var server => ResourceFormatter.Format(Strings.MusicServerDialog_SignInPassword_Format, server.UserName, server.BaseUrl)
    };

    /// <summary>
    ///     The added server's root folder, once <see cref="SubmitAsync" /> has added it.
    /// </summary>
    public Folder? AddedServer { get; private set; }

    /// <summary>
    ///     Sets the dialog up to sign in again to the server with root folder <paramref name="folderId" />.
    /// </summary>
    /// <returns>False if the folder isn't a server or couldn't be read.</returns>
    public async Task<bool> InitializeForSignInAsync(Guid folderId)
    {
        try
        {
            _signInServer = await _musicServers.GetServerAsync(folderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load server folder {FolderId} for signing in.", folderId);
        }

        if (_signInServer is null) return false;
        Kind = _signInServer.Kind;
        SignInWithApiKey = _signInServer.UsesApiKey;
        return true;
    }

    /// <returns>True once the server accepted the details; false with <see cref="ErrorMessage" /> set when it didn't.</returns>
    public async Task<bool> SubmitAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (_signInServer is { } server)
                await _musicServers.SignInAsync(server.FolderId, UsesApiKey ? ApiKey : Password);
            else if (UsesApiKey)
                AddedServer = await _musicServers.AddWithApiKeyAsync(Kind, Address, ApiKey);
            else
                AddedServer = await _musicServers.AddAsync(Kind, Address, UserName, Password);
            return true;
        }
        catch (MusicServerException ex)
        {
            _logger.LogWarning(ex, "The music server didn't accept the details.");
            ErrorMessage = MusicServerErrors.DescribeSignIn(ex.Error, Kind, UsesApiKey, _signInServer);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add or sign in to a music server.");
            ErrorMessage = Strings.MusicServer_Error_ServerError;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
