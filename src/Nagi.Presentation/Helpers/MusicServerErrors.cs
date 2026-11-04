using System.Threading.Tasks;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.Helpers;

public static class MusicServerErrors
{
    /// <summary>
    ///     Says what to do when a server in the library fails in a way that signing in again can't fix.
    /// </summary>
    /// <returns>The advice naming the server, or null when the error has no advice of its own.</returns>
    public static string? DescribeForServer(MusicServerError error, string serverName)
    {
        var format = error switch
        {
            MusicServerError.Unreachable => Strings.MusicServer_Error_Unreachable_Format,
            MusicServerError.SecureConnectionFailed => Strings.MusicServer_Error_SecureConnection_Format,
            MusicServerError.SignInMethodUnsupported => Strings.MusicServer_Error_SignInMethod_Format,
            _ => null
        };
        return format is null ? null : string.Format(format, serverName);
    }

    /// <summary>
    ///     Tells the user a server in the library failed and what to do about it, offering to sign in again when the
    ///     server rejected the saved sign-in.
    /// </summary>
    /// <param name="fallback">The message when there's no error or it has no advice of its own.</param>
    /// <returns>True when the user signed in again.</returns>
    public static async Task<bool> ShowAsync(IUIService ui, Guid folderId, string serverName, MusicServerError? error,
        string fallback)
    {
        if (error == MusicServerError.SignInFailed) return await OfferSignInAsync(ui, folderId, serverName);

        await ui.ShowMessageDialogAsync(Strings.Generic_Error,
            (error is { } known ? DescribeForServer(known, serverName) : null) ?? fallback);
        return false;
    }

    /// <returns>True when the user signed in again.</returns>
    public static async Task<bool> OfferSignInAsync(IUIService ui, Guid folderId, string serverName) =>
        await ui.ShowConfirmationDialogAsync(Strings.FolderPage_Menu_SignIn,
            string.Format(Strings.MusicServer_Error_SavedSignIn_Format, serverName), Strings.MusicServerDialog_SignInButton)
        && await ui.ShowMusicServerSignInDialogAsync(folderId);
}
