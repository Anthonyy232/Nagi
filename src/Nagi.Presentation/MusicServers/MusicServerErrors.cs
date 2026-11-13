using Microsoft.Extensions.Logging;
using Nagi.Core.MusicServers;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.MusicServers;

/// <summary>
///     Tells the user what went wrong with a music server and what to do about it.
/// </summary>
public static class MusicServerErrors
{
    /// <summary>
    ///     The message for a server that couldn't be added or signed in to again.
    /// </summary>
    /// <param name="usesApiKey">Whether the sign-in was with an API key rather than a password.</param>
    /// <param name="server">The server being signed in to again, or null while one is being added.</param>
    public static string DescribeSignIn(MusicServerError error, MusicServerKind kind, bool usesApiKey, MusicServer? server)
    {
        // Signing in again can't change the address or the way of signing in, so the advice is what to do instead.
        if (server is not null && DescribeForServer(error, server.Folder.Name) is { } advice) return advice;

        return error switch
        {
            MusicServerError.InvalidAddress => Strings.MusicServer_Error_InvalidAddress,
            MusicServerError.Unreachable => Strings.MusicServer_Error_Unreachable,
            MusicServerError.SecureConnectionFailed => Strings.MusicServer_Error_SecureConnectionFailed,
            MusicServerError.UnsupportedServer => kind == MusicServerKind.OpenSubsonic
                ? Strings.MusicServer_Error_NotOpenSubsonic
                : Strings.MusicServer_Error_NotJellyfin,
            MusicServerError.ServerTooOld => Strings.MusicServer_Error_JellyfinTooOld,
            MusicServerError.SignInFailed => usesApiKey
                ? Strings.MusicServer_Error_ApiKeyRejected
                : Strings.MusicServer_Error_SignInFailed,
            MusicServerError.SignInMethodUnsupported => usesApiKey
                ? Strings.MusicServer_Error_ApiKeyUnsupported
                : Strings.MusicServer_Error_PasswordUnsupported,
            MusicServerError.NoMusicLibrary => Strings.MusicServer_Error_NoMusicLibrary,
            MusicServerError.AlreadyAdded => Strings.MusicServer_Error_AlreadyAdded,
            // Only signing in again checks that an API key is the same account's.
            MusicServerError.DifferentAccount when server is not null =>
                ResourceFormatter.Format(Strings.MusicServer_Error_DifferentAccount_Format, server.UserName),
            MusicServerError.SignInNotSaved => Strings.Generic_SignInNotSaved,
            _ => Strings.MusicServer_Error_ServerError
        };
    }

    /// <summary>
    ///     Runs <paramref name="operation" /> on a server in the library and tells the user when it fails. When the
    ///     server rejected the saved sign-in, it offers to sign in again and then runs the operation again.
    /// </summary>
    /// <param name="failureMessage">The message for a failure that has no advice of its own.</param>
    public static async Task RunAsync(IUIService ui, ILogger logger, Guid folderId, string serverName, Func<Task> operation,
        string failureMessage)
    {
        while (true)
        {
            MusicServerError? error = null;
            try
            {
                await operation();
                return;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Cancelled an operation on server folder {FolderId}.", folderId);
                return;
            }
            catch (MusicServerException ex)
            {
                error = ex.Error;
                logger.LogWarning(ex, "An operation on server folder {FolderId} failed.", folderId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An operation on server folder {FolderId} failed.", folderId);
            }

            if (!await ShowAsync(ui, folderId, serverName, error, failureMessage)) return;
        }
    }

    /// <summary>
    ///     Tells the user a server in the library failed and what to do about it, offering to sign in again when the
    ///     server rejected the saved sign-in.
    /// </summary>
    /// <param name="error">What went wrong, or null when it isn't known.</param>
    /// <param name="failureMessage">The message for a failure that has no advice of its own.</param>
    /// <returns>True when the user signed in again.</returns>
    public static async Task<bool> ShowAsync(IUIService ui, Guid folderId, string serverName, MusicServerError? error,
        string failureMessage)
    {
        if (error == MusicServerError.SignInFailed) return await OfferSignInAsync(ui, folderId, serverName);

        await ui.ShowMessageDialogAsync(Strings.Generic_Error,
            (error is { } known ? DescribeForServer(known, serverName) : null) ?? failureMessage);
        return false;
    }

    /// <returns>True when the user signed in again.</returns>
    public static async Task<bool> OfferSignInAsync(IUIService ui, Guid folderId, string serverName) =>
        await ui.ShowConfirmationDialogAsync(Strings.FolderPage_Menu_SignIn,
            ResourceFormatter.Format(Strings.MusicServer_Error_SavedSignIn_Format, serverName), Strings.MusicServerDialog_SignInButton)
        && await ui.ShowMusicServerSignInDialogAsync(folderId);

    /// <summary>
    ///     Advice for a failure of a server in the library that signing in again can't fix, and that the user can do
    ///     something about.
    /// </summary>
    /// <returns>The advice, or null for a failure without any.</returns>
    public static string? DescribeForServer(MusicServerError error, string serverName)
    {
        var format = error switch
        {
            MusicServerError.Unreachable => Strings.MusicServer_Error_Unreachable_Format,
            MusicServerError.SecureConnectionFailed => Strings.MusicServer_Error_SecureConnection_Format,
            MusicServerError.SignInMethodUnsupported => Strings.MusicServer_Error_SignInMethod_Format,
            _ => null
        };
        return format is null ? null : ResourceFormatter.Format(format, serverName);
    }
}
