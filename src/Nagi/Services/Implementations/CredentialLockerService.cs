using Nagi.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Linq;
using Windows.Security.Credentials;

namespace Nagi.Services.Implementations;

/// <summary>
/// Implements credential management using the Windows PasswordVault for secure storage.
/// </summary>
public class CredentialLockerService : ICredentialLockerService {
    private readonly PasswordVault _vault = new();

    // The HResult for the "Element not found" exception, which PasswordVault
    // throws when a resource is not found. We handle this explicitly as it's an expected condition.
    private const int ELEMENT_NOT_FOUND_HRESULT = unchecked((int)0x80070490);

    /// <inheritdoc />
    public void SaveCredential(string resource, string userName, string password) {
        try {
            // To ensure a clean save and prevent errors if a credential already exists,
            // remove any existing credential for the resource before adding the new one.
            RemoveCredential(resource);

            var credential = new PasswordCredential(resource, userName, password);
            _vault.Add(credential);
            Debug.WriteLine($"[INFO] CredentialLockerService: Saved credential for resource '{resource}'.");
        }
        catch (Exception ex) {
            // Log any unexpected errors during the save operation.
            Debug.WriteLine($"[ERROR] CredentialLockerService: Failed to save credential for resource '{resource}'. Error: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public (string? UserName, string? Password)? RetrieveCredential(string resource) {
        try {
            // Find the first credential matching the resource.
            var credential = _vault.FindAllByResource(resource).FirstOrDefault();

            if (credential != null) {
                // The password is not loaded by default and must be explicitly retrieved.
                credential.RetrievePassword();
                return (credential.UserName, credential.Password);
            }

            return null;
        }
        // This is an expected condition when the credential does not exist.
        // We catch it and return null without logging an error.
        catch (Exception ex) when (ex.HResult == ELEMENT_NOT_FOUND_HRESULT) {
            return null;
        }
        // Catch any other, truly unexpected exceptions.
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] CredentialLockerService: Failed to retrieve credential for resource '{resource}'. Error: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public void RemoveCredential(string resource) {
        try {
            // Find all credentials associated with the resource, as the username is unknown.
            var credentials = _vault.FindAllByResource(resource);
            foreach (var credential in credentials) {
                _vault.Remove(credential);
            }
        }

        catch (Exception ex) when (ex.HResult == ELEMENT_NOT_FOUND_HRESULT) {
            // Silently ignore, as this is not an error.
        }
        // Catch other exceptions that might occur during removal.
        catch (Exception ex) {
            Debug.WriteLine($"[WARN] CredentialLockerService: Issue during removal of credential for resource '{resource}'. Error: {ex.Message}");
        }
    }
}