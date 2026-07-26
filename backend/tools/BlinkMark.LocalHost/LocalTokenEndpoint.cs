using BlinkMark.TestHost;

namespace BlinkMark.LocalHost;

/// <summary>
/// The local token endpoint.
/// </summary>
/// <remarks>
/// Stands in for Entra, and only for Entra. It hands out genuine JWTs that the production
/// authentication handler then validates in full — issuer, audience, lifetime, signature,
/// tenant, scope, and the app-only refusal required by FR-055. Nothing downstream can tell that
/// the token came from here rather than from Microsoft.
/// <para>
/// That is why this exists instead of a stub authentication handler. A stub would have made the
/// local host easier to build and useless for the thing you most want to try by hand: proving
/// that a colleague cannot delete your file, that an expired token is refused, and that a token
/// from another organization gets nothing. Those all still work here, and they work because the
/// real code rejects them.
/// </para>
/// <para>
/// This type lives in a project that is never deployed. There is no configuration flag guarding
/// it, because there is nothing to guard: it is not compiled into the API image at all.
/// </para>
/// </remarks>
public static class LocalTokenEndpoint
{
    public static IEndpointRouteBuilder MapLocalTokenEndpoint(
        this IEndpointRouteBuilder endpoints,
        TestTokenIssuer tokens)
    {
        endpoints.MapGet("/dev/token", (string? user, string? agent, int? minutes) =>
            {
                var userId = string.IsNullOrWhiteSpace(user) ? "local-dev-user" : user;
                var displayName = ToDisplayName(userId);
                var expiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes ?? 480);

                // Two identities are worth being able to mint by hand. A second user is how you
                // check that ownership actually holds — upload as alice, then try to delete as
                // bob and watch it refuse. An agent token is how you check that agent attribution
                // survives into the audit trail.
                var token = string.IsNullOrWhiteSpace(agent)
                    ? tokens.IssueUserToken(userId, displayName, expiresAt)
                    : tokens.IssueAgentToken(userId, displayName, agent, expiresAt);

                return Results.Ok(new
                {
                    accessToken = token,
                    userId,
                    displayName,
                    actingAgentId = agent,
                    expiresAt,
                    usage = $"Authorization: Bearer <accessToken>",
                });
            })
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithName("LocalDevToken");

        return endpoints;
    }

    /// <summary>Turns "alice" into "Alice", so the UI has something human to show.</summary>
    private static string ToDisplayName(string userId)
    {
        var cleaned = userId.Replace('-', ' ').Replace('.', ' ').Trim();

        return string.Join(
            ' ',
            cleaned
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
