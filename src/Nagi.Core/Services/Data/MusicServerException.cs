namespace Nagi.Core.Services.Data;

public enum MusicServerError
{
    /// <summary>The address isn't an http or https URL of a server, or it contains a user name or query.</summary>
    InvalidAddress,

    Unreachable,

    /// <summary>The HTTPS connection failed, for example because the server only speaks HTTP or its certificate isn't trusted.</summary>
    SecureConnectionFailed,

    /// <summary>The address answers, but not as the kind of server expected, for example a Subsonic server without OpenSubsonic.</summary>
    UnsupportedServer,

    ServerTooOld,

    /// <summary>The server rejected the credentials, or a saved sign-in is no longer valid.</summary>
    SignInFailed,

    /// <summary>The server doesn't accept this way of signing in, such as a password when it needs an API key.</summary>
    SignInMethodUnsupported,

    NoMusicLibrary,

    /// <summary>The same account on the same server is already in the library.</summary>
    AlreadyAdded,

    /// <summary>A new API key belongs to another account than the one the server was added with.</summary>
    DifferentAccount,

    /// <summary>The operating system wouldn't store the sign-in, for example because its credential store is full.</summary>
    SignInNotSaved,

    /// <summary>The song or image no longer exists on the server, or the server is no longer in the library.</summary>
    NotFound,

    /// <summary>Any other error the server answered with, such as an HTTP 500.</summary>
    ServerError
}

/// <summary>
///     Thrown when a music server can't be added, signed in to or used; <see cref="Error" /> says why.
/// </summary>
public sealed class MusicServerException(MusicServerError error, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public MusicServerError Error { get; } = error;
}
