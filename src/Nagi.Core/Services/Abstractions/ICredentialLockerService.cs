namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Stores secrets in the operating system's credential store, such as the Windows Credential Manager.
/// </summary>
public interface ICredentialLockerService
{
    /// <summary>
    ///     Overwrites any credential already saved for <paramref name="resource" />.
    /// </summary>
    /// <param name="resource">A unique identifier for the credential resource (e.g., "AppName/Service").</param>
    void SaveCredential(string resource, string userName, string password);

    /// <returns>A tuple containing the username and password, or null if not found.</returns>
    (string? UserName, string? Password)? RetrieveCredential(string resource);

    /// <summary>
    ///     Does nothing if no credential is saved for <paramref name="resource" />.
    /// </summary>
    void RemoveCredential(string resource);
}
