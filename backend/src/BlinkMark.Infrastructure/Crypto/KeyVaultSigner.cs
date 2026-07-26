using Azure.Core;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using BlinkMark.Core.Abstractions;
using BlinkMark.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace BlinkMark.Infrastructure.Crypto;

/// <summary>
/// Signs preview tokens with a key that never leaves Key Vault (T133).
/// </summary>
/// <remarks>
/// The distinction this class exists to preserve is between "give me the signing key" and "sign
/// this for me". BlinkMark only ever does the second. The identity holds Key Vault <em>Crypto
/// User</em>, which permits sign and verify and does not permit export, so the private key is
/// not present in this process at any point — not in configuration, not in memory, not in a
/// crash dump (Principle VII, research.md R13/R14).
/// <para>
/// The cost is a network round trip per signature. That is acceptable because a preview token is
/// minted once per file metadata read, not once per request, and the alternative is a retrievable
/// secret that would need rotating and could be stolen.
/// </para>
/// </remarks>
public sealed class KeyVaultSigner : ITokenSigner
{
    private readonly KeyClient _keyClient;
    private readonly TokenCredential _credential;
    private readonly string _keyName;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CryptographyClient? _cryptographyClient;
    private string? _keyId;

    public KeyVaultSigner(KeyClient keyClient, TokenCredential credential, IOptions<BlinkMarkOptions> options)
    {
        _keyClient = keyClient;
        _credential = credential;
        _keyName = options.Value.Preview.SigningKeyName;
    }

    /// <summary>ES256 — ECDSA over P-256 with SHA-256, matching the key in keyvault.bicep.</summary>
    public string Algorithm => "ES256";

    public async Task<string> GetKeyIdAsync(CancellationToken cancellationToken = default)
    {
        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        return _keyId!;
    }

    public async Task<byte[]> SignAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);

        var result = await _cryptographyClient!
            .SignDataAsync(SignatureAlgorithm.ES256, payload.ToArray(), cancellationToken)
            .ConfigureAwait(false);

        return result.Signature;
    }

    public async Task<bool> VerifyAsync(
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken = default)
    {
        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);

        var result = await _cryptographyClient!
            .VerifyDataAsync(SignatureAlgorithm.ES256, payload.ToArray(), signature.ToArray(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsValid;
    }

    private async Task EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_cryptographyClient is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cryptographyClient is not null)
            {
                return;
            }

            var key = await _keyClient.GetKeyAsync(_keyName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Pinned to the specific key version. Without this, a key rotation would silently
            // invalidate every preview token already in a user's browser.
            _keyId = key.Value.Id.ToString();
            _cryptographyClient = new CryptographyClient(key.Value.Id, _credential);
        }
        finally
        {
            _gate.Release();
        }
    }
}
