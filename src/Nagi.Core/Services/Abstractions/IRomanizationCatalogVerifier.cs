using Nagi.Core.Models.Romanization;

namespace Nagi.Core.Services.Abstractions;

public interface IRomanizationCatalogVerifier
{
    bool Verify(RomanizationCatalogEnvelope envelope);
}
