using Nagi.Core.Models.Romanization;

namespace Nagi.Core.Services.Abstractions;

public interface IRomanizationPackManager
{
    event Action? PacksChanged;

    Task<IReadOnlyList<RomanizationPackView>> GetAvailablePacksAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstalledRomanizationPack>> GetInstalledPacksAsync(CancellationToken cancellationToken = default);
    Task<RomanizationPackOperationResult> InstallPackAsync(string packId, CancellationToken cancellationToken = default);
    Task<RomanizationPackOperationResult> RemovePackAsync(string packId, CancellationToken cancellationToken = default);
}
