using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlinkMark.Core.Abstractions;

namespace BlinkMark.Core.Preview;

/// <summary>
/// Issues and validates preview tokens (T118).
/// </summary>
/// <remarks>
/// This type exists because isolating the preview onto its own origin — which Principle IV
/// requires — removes the session that Principle I relies on. The framed document is sandboxed
/// without <c>allow-same-origin</c>, so it renders in an opaque origin: it cannot read or set
/// cookies, cannot use storage, and cannot attach an <c>Authorization</c> header to its own
/// document request. There is no cookie- or header-based option that preserves the isolation.
/// <para>
/// So the credential travels in the URL, in deliberately the same shape the constitution already
/// sanctions for blob access: a short-lived, user-scoped, read-only grant with a 15-minute
/// ceiling. It is scoped to one file and one render version, and it confers no ability to
/// comment, delete, extend retention, or enumerate anything.
/// </para>
/// <para>
/// It also fixes an auditing problem nobody had noticed. FR-041 lists <c>view</c> and
/// <c>preview</c> as separate actions because they happen in different services, and only the
/// preview host can know whether an issued token was ever redeemed.
/// </para>
/// <para>
/// Signing goes through <see cref="ITokenSigner"/>, which signs in place inside Key Vault. The
/// signing key is never in this process (research.md R13, R14).
/// </para>
/// </remarks>
public sealed class PreviewTokenService(
    ITokenSigner signer,
    IClock clock,
    PreviewTokenOptions options)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The hard ceiling on a preview token's life.
    /// </summary>
    /// <remarks>
    /// Configuration can shorten this and cannot lengthen it. A ceiling that a settings file can
    /// raise is not a ceiling.
    /// </remarks>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The <c>typ</c> header value every preview token carries.
    /// </summary>
    /// <remarks>
    /// A distinct media type rather than the generic <c>JWT</c>, so a preview token cannot be
    /// replayed anywhere else and nothing else can be replayed here. Token-type confusion is the
    /// same class of bug as algorithm confusion, and the same fix applies: state what we are
    /// willing to accept instead of taking whatever arrives.
    /// </remarks>
    public const string TokenType = "blinkmark-preview+jwt";

    /// <summary>Tolerance for clock drift between the API and the preview host.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly ITokenSigner _signer = signer;
    private readonly IClock _clock = clock;
    private readonly PreviewTokenOptions _options = options;

    /// <summary>Mints a token granting read of one render of one file.</summary>
    public async Task<string> IssueAsync(
        string fileId,
        string renderVersion,
        string subjectId,
        string correlationId,
        string? actingAgentId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var now = _clock.UtcNow;
        var lifetime = _options.Lifetime > MaximumLifetime ? MaximumLifetime : _options.Lifetime;

        var claims = new PreviewTokenClaims
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = subjectId,
            ActingAgentId = actingAgentId,
            FileId = fileId,
            RenderVersion = renderVersion,
            IssuedAt = now.ToUnixTimeSeconds(),
            NotBefore = now.ToUnixTimeSeconds(),
            ExpiresAt = (now + lifetime).ToUnixTimeSeconds(),
            CorrelationId = correlationId,
            TokenId = Models.Identifiers.New(now),
        };

        var keyId = await _signer.GetKeyIdAsync(cancellationToken).ConfigureAwait(false);

        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            new PreviewTokenHeader { Algorithm = _signer.Algorithm, Type = TokenType, KeyId = keyId },
            SerializerOptions));

        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims, SerializerOptions));

        var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");
        var signature = await _signer.SignAsync(signingInput, cancellationToken).ConfigureAwait(false);

        return $"{header}.{payload}.{Base64UrlEncode(signature)}";
    }

    /// <summary>
    /// Validates a token against the requested file.
    /// </summary>
    /// <remarks>
    /// Signature, audience, expiry, and file match — and nothing else. The preview host
    /// deliberately establishes identity no other way: it never receives an Entra token, a
    /// session cookie, or a refresh token, so there is nothing else for it to consult.
    /// </remarks>
    public async Task<PreviewTokenValidationResult> ValidateAsync(
        string? token,
        string requestedFileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.Missing);
        }

        var segments = token.Split('.');
        if (segments.Length != 3)
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.Malformed);
        }

        PreviewTokenHeader? header;
        PreviewTokenClaims? claims;
        try
        {
            header = JsonSerializer.Deserialize<PreviewTokenHeader>(Base64UrlDecode(segments[0]));
            claims = JsonSerializer.Deserialize<PreviewTokenClaims>(Base64UrlDecode(segments[1]));
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.Malformed);
        }

        if (header is null || claims is null)
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.Malformed);
        }

        // Algorithm confusion is the classic JWT bug. The algorithm comes from what we are
        // willing to verify, never from what the token asked for.
        if (!string.Equals(header.Algorithm, _signer.Algorithm, StringComparison.Ordinal))
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.Malformed);
        }

        // Token-type confusion is the same bug wearing a different hat. Only a token minted as a
        // preview token opens the preview host.
        if (!string.Equals(header.Type, TokenType, StringComparison.Ordinal))
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.WrongType);
        }

        // A token that names no key was signed by something that did not know which key it was
        // using, which is not a state worth reasoning about.
        if (string.IsNullOrWhiteSpace(header.KeyId))
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.Malformed);
        }

        byte[] signature;
        try
        {
            signature = Base64UrlDecode(segments[2]);
        }
        catch (FormatException)
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.Malformed);
        }

        var signingInput = Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}");
        var signatureValid = await _signer
            .VerifyAsync(signingInput, signature, cancellationToken)
            .ConfigureAwait(false);

        if (!signatureValid)
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.BadSignature);
        }

        // A token minted for the API must not open the preview host, and vice versa. Without an
        // audience check, the isolation between the two origins would be cosmetic.
        if (!string.Equals(claims.Audience, _options.Audience, StringComparison.Ordinal))
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.WrongAudience);
        }

        if (!string.Equals(claims.Issuer, _options.Issuer, StringComparison.Ordinal))
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.WrongIssuer);
        }

        var now = _clock.UtcNow.ToUnixTimeSeconds();
        var skew = (long)ClockSkew.TotalSeconds;

        if (claims.ExpiresAt <= now)
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.Expired);
        }

        // Not yet valid, and not issued in the future. Both would mean the token came from a
        // clock we do not control, which is worth refusing rather than tolerating.
        if (claims.NotBefore > now + skew || claims.IssuedAt > now + skew)
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.NotYetValid);
        }

        // Defence against a token minted with a longer life than the ceiling permits, whether by
        // a misconfigured issuer or a future code path.
        if (claims.ExpiresAt - claims.IssuedAt > (long)MaximumLifetime.TotalSeconds)
        {
            return PreviewTokenValidationResult.Unauthorized(PreviewTokenFailure.Expired);
        }

        // A valid token for a different file is a 403, not a 401: the caller is authenticated,
        // it is simply asking for something this grant does not cover.
        if (!string.Equals(claims.FileId, requestedFileId, StringComparison.Ordinal))
        {
            return PreviewTokenValidationResult.Forbidden(claims);
        }

        return PreviewTokenValidationResult.Success(claims);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) => Base64Url.EncodeToString(value);

    private static byte[] Base64UrlDecode(string value) => Base64Url.DecodeFromChars(value);
}

/// <summary>Configuration for <see cref="PreviewTokenService"/>.</summary>
public sealed record PreviewTokenOptions
{
    /// <summary>The API host that mints tokens.</summary>
    public required string Issuer { get; init; }

    /// <summary>The preview host that redeems them.</summary>
    public required string Audience { get; init; }

    /// <summary>
    /// Token lifetime, capped at <see cref="PreviewTokenService.MaximumLifetime"/>.
    /// </summary>
    public TimeSpan Lifetime { get; init; } = PreviewTokenService.MaximumLifetime;
}

/// <summary>JOSE header.</summary>
public sealed record PreviewTokenHeader
{
    [JsonPropertyName("alg")]
    public required string Algorithm { get; init; }

    [JsonPropertyName("typ")]
    public string Type { get; init; } = PreviewTokenService.TokenType;

    [JsonPropertyName("kid")]
    public required string KeyId { get; init; }
}

/// <summary>Preview token claims, per contracts/preview-origin.md.</summary>
public sealed record PreviewTokenClaims
{
    [JsonPropertyName("iss")]
    public required string Issuer { get; init; }

    [JsonPropertyName("aud")]
    public required string Audience { get; init; }

    /// <summary>Entra object id of the user the preview is for.</summary>
    [JsonPropertyName("sub")]
    public required string Subject { get; init; }

    /// <summary>Acting agent identifier, absent for a direct user action (FR-048, FR-049).</summary>
    [JsonPropertyName("agt")]
    public string? ActingAgentId { get; init; }

    /// <summary>The single file this token grants. One token never covers two files.</summary>
    [JsonPropertyName("fid")]
    public required string FileId { get; init; }

    /// <summary>Pins which render artifact may be served.</summary>
    [JsonPropertyName("rv")]
    public required string RenderVersion { get; init; }

    [JsonPropertyName("iat")]
    public required long IssuedAt { get; init; }

    [JsonPropertyName("nbf")]
    public required long NotBefore { get; init; }

    [JsonPropertyName("exp")]
    public required long ExpiresAt { get; init; }

    /// <summary>Propagated from the originating API request (Principle V).</summary>
    [JsonPropertyName("cid")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("jti")]
    public required string TokenId { get; init; }
}

/// <summary>Why a preview token was refused.</summary>
public enum PreviewTokenFailure
{
    None,
    Missing,
    Malformed,
    BadSignature,
    WrongAudience,
    WrongIssuer,
    Expired,

    /// <summary>Presented before its not-before time, or issued by a clock ahead of ours.</summary>
    NotYetValid,

    /// <summary>Not minted as a preview token.</summary>
    WrongType,

    /// <summary>Valid, but minted for a different file.</summary>
    FileMismatch,
}

/// <summary>The outcome of validating a preview token.</summary>
public sealed record PreviewTokenValidationResult
{
    public required bool IsValid { get; init; }

    public required PreviewTokenFailure Failure { get; init; }

    public PreviewTokenClaims? Claims { get; init; }

    /// <summary>
    /// The status the preview host should return.
    /// </summary>
    /// <remarks>
    /// A 401 says nothing at all — no content, no filename, no confirmation the file exists
    /// (FR-001). A 403 is only reachable with a valid token, so it may safely admit that the
    /// grant does not cover this file.
    /// </remarks>
    public required int StatusCode { get; init; }

    public static PreviewTokenValidationResult Success(PreviewTokenClaims claims) => new()
    {
        IsValid = true,
        Failure = PreviewTokenFailure.None,
        Claims = claims,
        StatusCode = 200,
    };

    public static PreviewTokenValidationResult Unauthorized(PreviewTokenFailure failure) => new()
    {
        IsValid = false,
        Failure = failure,
        Claims = null,
        StatusCode = 401,
    };

    public static PreviewTokenValidationResult Forbidden(PreviewTokenClaims claims) => new()
    {
        IsValid = false,
        Failure = PreviewTokenFailure.FileMismatch,
        Claims = claims,
        StatusCode = 403,
    };
}
