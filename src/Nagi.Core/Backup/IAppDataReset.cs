namespace Nagi.Core.Backup;

public interface IAppDataReset
{
    /// <summary>
    ///     Returns the app to a fresh install: clears the queue, signs out of the scrobbling accounts, resets every
    ///     setting, turns launching at sign-in off, and deletes the library, its caches, the saved session and the
    ///     romanization packs. If the services fail, it clears the settings store and deletes the data files directly
    ///     instead, so it doesn't throw. The app should restart afterwards.
    /// </summary>
    Task ResetAsync();
}
