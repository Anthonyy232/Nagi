using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nagi.Core.Models.Romanization;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Romanization;

public sealed class EcdsaRomanizationCatalogVerifier : IRomanizationCatalogVerifier
{
    private const string CatalogPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4pRJVkt6t/CuPmEnp/90Avby/v6H
        us3l3XDET+Idy6D2EVqx0ZoInvmvuu6Gc5KvXuykypbMVZiSiBvNH0G0pQ==
        -----END PUBLIC KEY-----
        """;

    private static readonly JsonSerializerOptions _jsonOptions = RomanizationJson.Options;

    public bool Verify(RomanizationCatalogEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.Signature)) return false;

        try
        {
            var signature = Convert.FromBase64String(envelope.Signature);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope.Catalog, _jsonOptions));

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(CatalogPublicKeyPem);
            return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch
        {
            return false;
        }
    }
}
