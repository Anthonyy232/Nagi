using Nagi.Core.Models.Romanization;

namespace Nagi.Core.Services.Abstractions;

public interface IRomanizationProvider
{
    string EngineId { get; }

    bool Supports(string text);

    Task<string?> RomanizeAsync(string text, InstalledRomanizationPack pack, CancellationToken cancellationToken = default);

    Task<bool> ValidatePackAsync(InstalledRomanizationPack pack, CancellationToken cancellationToken = default);
}
