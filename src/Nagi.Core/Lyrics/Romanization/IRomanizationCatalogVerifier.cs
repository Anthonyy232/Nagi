namespace Nagi.Core.Lyrics.Romanization;

public interface IRomanizationCatalogVerifier
{
    bool Verify(RomanizationCatalogEnvelope envelope);
}
