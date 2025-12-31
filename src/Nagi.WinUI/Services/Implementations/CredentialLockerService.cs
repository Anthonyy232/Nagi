using System;
using System.Linq;
using Windows.Security.Credentials;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Implements credential management using the Windows PasswordVault for secure storage.
/// </summary>
public class CredentialLockerService : ICredentialLockerService
{
    // The HResult for the "Element not found" exception, which PasswordVault
    // throws when a resource is not found. We handle this explicitly as it's an expected condition.
    private const int ELEMENT_NOT_FOUND_HRESULT = unchecked((int)0x80070490);
    private readonly ILogger<CredentialLockerService> _logger;

    private readonly PasswordVault _vault = new();

    public CredentialLockerService(ILogger<CredentialLockerService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void SaveCredential(string resource, string userName, string password)
    {
        try
        {
            // To ensure a clean save and prevent errors if a credential already exists,
            // remove any existing credential for the resource before adding the new one.
            RemoveCredential(resource);

            var credential = new PasswordCredential(resource, userName, password);
            _vault.Add(credential);

            _logger.LogDebug("Saved credential for resource {Resource}", resource);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save credential for resource {Resource}", resource);
        }
    }

    /// <inheritdoc />
    public (string? UserName, string? Password)? RetrieveCredential(string resource)
    {
        try
        {
            var credential = _vault.RetrieveAll().FirstOrDefault(c => c.Resource == resource);

            if (credential != null)
            {
                credential.RetrievePassword();
                _logger.LogDebug("Retrieved credential for resource {Resource}", resource);
                return (credential.UserName, credential.Password);
            }

            _logger.LogDebug("No credential found for resource {Resource}", resource);
            return null;
        }
        catch (Exception ex) when (ex.HResult == ELEMENT_NOT_FOUND_HRESULT)
        {
            // This is an expected condition when the credential does not exist.
            // We catch it and return null without logging an error.
            _logger.LogDebug("No credential found for resource {Resource} (HResult match)", resource);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve credential for resource {Resource}", resource);
            return null;
        }
    }

    /// <inheritdoc />
    public void RemoveCredential(string resource)
    {
        try
        {
            var credentials = _vault.RetrieveAll().Where(c => c.Resource == resource).ToList();
            if (credentials.Count == 0) return;

            foreach (var credential in credentials) _vault.Remove(credential);
            _logger.LogDebug("Removed {Count} credential(s) for resource {Resource}", credentials.Count, resource);
        }
        catch (Exception ex) when (ex.HResult == ELEMENT_NOT_FOUND_HRESULT)
        {
            // Silently ignore, as this is not an error. The credential was already gone.
        }
        catch (Exception ex)
        {
            // A warning is appropriate here as it's not a critical failure, but it is unexpected.
            _logger.LogWarning(ex, "An issue occurred during removal of credential for resource {Resource}", resource);
        }
    }
}