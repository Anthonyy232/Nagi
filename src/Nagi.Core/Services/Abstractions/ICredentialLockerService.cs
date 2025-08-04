namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a service for securely storing and retrieving secrets
///     using the Windows Credential Manager.
/// </summary>
public interface ICredentialLockerService
{
    /// <summary>
    ///     Saves a credential to the Windows Credential Manager.
    ///     If a credential for the given resource already exists, it will be overwritten.
    /// </summary>
    /// <param name="resource">A unique identifier for the credential resource (e.g., "AppName/Service").</param>
    /// <param name="userName">The username associated with the credential.</param>
    /// <param name="password">The secret/password to store.</param>
    void SaveCredential(string resource, string userName, string password);

    /// <summary>
    ///     Retrieves a credential from the Windows Credential Manager.
    /// </summary>
    /// <param name="resource">The unique identifier for the credential resource.</param>
    /// <returns>A tuple containing the username and password, or null if not found.</returns>
    (string? UserName, string? Password)? RetrieveCredential(string resource);

    /// <summary>
    ///     Removes a credential from the Windows Credential Manager.
    ///     This method is idempotent; it will not fail if the credential does not exist.
    /// </summary>
    /// <param name="resource">The unique identifier for the credential resource.</param>
    void RemoveCredential(string resource);
}