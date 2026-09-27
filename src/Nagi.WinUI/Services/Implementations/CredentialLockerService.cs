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
            var credential = new PasswordCredential(resource, userName, password);
            _vault.Add(credential);

            // Keep the previous credential until the replacement has been saved successfully.
            foreach (var previous in _vault.FindAllByResource(resource))
                if (previous.UserName != userName) _vault.Remove(previous);

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
            var credential = _vault.FindAllByResource(resource).FirstOrDefault();

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
            var credentials = _vault.FindAllByResource(resource);

            foreach (var credential in credentials) _vault.Remove(credential);
            _logger.LogDebug("Removed {Count} credential(s) for resource {Resource}", credentials.Count, resource);
        }
        catch (Exception ex) when (ex.HResult == ELEMENT_NOT_FOUND_HRESULT)
        {
            // Silently ignore, as this is not an error. The credential was already gone.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An issue occurred during removal of credential for resource {Resource}", resource);
        }
    }
}
