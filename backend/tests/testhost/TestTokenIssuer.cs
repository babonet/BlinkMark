using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace BlinkMark.TestHost;

/// <summary>
/// Mints access tokens for tests.
/// </summary>
/// <remarks>
/// Tests use a symmetric signing key rather than a stubbed authentication handler, and that is a
/// deliberate choice. A stub handler would bypass the very validation the mandatory
/// authorization tests exist to prove: that the issuer is pinned to one tenant, that the audience
/// is pinned to this API, and that an expired token is refused. With a real token going through
/// the real handler, a cross-tenant test fails for the right reason — the production code
/// rejected it — rather than because a fake was told to say no.
/// </remarks>
public sealed class TestTokenIssuer(string tenantId, string audience, string? spaClientId = null)
{
    public const string SigningKeyMaterial = "blinkmark-test-signing-key-not-a-secret-0123456789";

    /// <summary>The scopes this API publishes. A token without one of these is refused.</summary>
    public const string DefaultScopes = "Files.ReadWrite Comments.ReadWrite";

    public static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes(SigningKeyMaterial));

    public string TenantId { get; } = tenantId;

    public string Audience { get; } = audience;

    /// <summary>The approved client. Tokens default to being obtained by it.</summary>
    public string SpaClientId { get; } = spaClientId ?? "blinkmark-test-spa";

    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>A token for an ordinary signed-in member of the owning organization.</summary>
    public string IssueUserToken(
        string userId,
        string displayName,
        DateTimeOffset? expiresAt = null,
        string? issuerOverride = null,
        string? audienceOverride = null,
        string? scopesOverride = null,
        string? authorizedPartyOverride = null)
    {
        return Issue(
            userId,
            displayName,
            actingAgentId: null,
            expiresAt,
            issuerOverride,
            audienceOverride,
            scopesOverride ?? DefaultScopes,
            authorizedPartyOverride ?? SpaClientId);
    }

    /// <summary>A token obtained by an agent through On-Behalf-Of, representing a user.</summary>
    public string IssueAgentToken(
        string userId,
        string displayName,
        string agentId,
        DateTimeOffset? expiresAt = null)
    {
        return Issue(userId, displayName, agentId, expiresAt, null, null, DefaultScopes, agentId);
    }

    /// <summary>
    /// An application-only token: no delegated scope, so nobody is signed in behind it.
    /// </summary>
    /// <remarks>
    /// FR-055 forbids this shape outright — there is no standing agent identity and nothing acts
    /// for a signed-out user. The API must refuse it before any endpoint sees it.
    /// </remarks>
    public string IssueAppOnlyToken(string applicationId)
    {
        var now = DateTimeOffset.UtcNow;

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("oid", applicationId),
                new Claim("sub", applicationId),
                new Claim("tid", TenantId),
                new Claim("ver", "2.0"),
                new Claim("azp", applicationId),
                new Claim("idtyp", "app"),
                // Application permissions, and deliberately no `scp`.
                new Claim("roles", "Files.ReadWrite.All"),
            ],
            notBefore: now.UtcDateTime.AddMinutes(-1),
            expires: now.UtcDateTime.AddHours(1),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string Issue(
        string userId,
        string displayName,
        string? actingAgentId,
        DateTimeOffset? expiresAt,
        string? issuerOverride,
        string? audienceOverride,
        string scopes,
        string authorizedParty)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = expiresAt ?? now.AddHours(1);

        // An already-expired token still needs a coherent notBefore, or the handler refuses to
        // construct it and the test fails for the wrong reason.
        var notBefore = expires < now ? expires.AddMinutes(-5) : now.AddMinutes(-1);

        var claims = new List<Claim>
        {
            new("oid", userId),
            new("sub", userId),
            new("name", displayName),
            new("tid", TenantId),
            new("ver", "2.0"),
            new("azp", authorizedParty),
            new("preferred_username", $"{displayName.Replace(" ", ".", StringComparison.Ordinal)}@contoso.com"),
        };

        if (!string.IsNullOrWhiteSpace(scopes))
        {
            claims.Add(new Claim("scp", scopes));
        }

        if (actingAgentId is not null)
        {
            claims.Add(new Claim("act_agt", actingAgentId));
        }

        var token = new JwtSecurityToken(
            issuer: issuerOverride ?? Issuer,
            audience: audienceOverride ?? Audience,
            claims: claims,
            notBefore: notBefore.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
