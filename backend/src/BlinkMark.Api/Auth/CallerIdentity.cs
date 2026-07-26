using System.Security.Claims;
using BlinkMark.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace BlinkMark.Api.Auth;

/// <summary>
/// The caller, as established by the token and nothing else.
/// </summary>
/// <remarks>
/// Every identity in BlinkMark comes from here. A request body that names an author, an owner,
/// or an acting agent is ignored — that is FR-005 and FR-023, and it is the single most
/// important habit in the codebase.
/// </remarks>
public sealed record CallerIdentity
{
    /// <summary>Entra object id.</summary>
    public required string UserId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Tenant the token was issued by. Pinned during validation; carried for auditing.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Set when an agent is acting for this user under On-Behalf-Of.
    /// </summary>
    /// <remarks>
    /// Under OBO the API sees the user's identity and applies its ordinary authorization path,
    /// so this value never widens what the caller may do. It exists so that attribution and the
    /// audit trail can say who was driving (FR-048, FR-049).
    /// </remarks>
    public string? ActingAgentId { get; init; }

    public bool IsAgentInitiated => ActingAgentId is not null;
}

/// <summary>Reads a <see cref="CallerIdentity"/> from a validated principal.</summary>
public static class CallerIdentityExtensions
{
    private const string ObjectIdClaim = "oid";
    private const string LegacyObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string TenantIdClaim = "tid";
    private const string AzpClaim = "azp";
    private const string AppIdClaim = "appid";

    /// <summary>
    /// Builds the caller identity for the current request.
    /// </summary>
    /// <remarks>
    /// Reads the SPA's application id from configuration so that agent-initiated requests can be
    /// told apart from direct ones. Everything else comes from the token.
    /// </remarks>
    public static CallerIdentity? GetCaller(this HttpContext context)
    {
        var entra = context.RequestServices
            .GetRequiredService<IOptions<BlinkMarkOptions>>()
            .Value.Entra;

        return context.User.GetCaller(entra.SpaClientId);
    }

    /// <summary>Returns the caller, or throws if the pipeline let an unauthenticated request through.</summary>
    public static CallerIdentity RequireCaller(this HttpContext context) =>
        context.GetCaller()
        ?? throw new UnauthorizedAccessException("The request reached an authorized endpoint without a usable identity.");

    /// <summary>
    /// Builds the caller identity, or returns <see langword="null"/> if the principal is not
    /// usable.
    /// </summary>
    /// <param name="principal">A principal that has already passed token validation.</param>
    /// <param name="spaClientId">
    /// The SPA's application id. When supplied, any other authorized party marks the request as
    /// agent-initiated.
    /// </param>
    public static CallerIdentity? GetCaller(this ClaimsPrincipal? principal, string? spaClientId = null)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userId = principal.FindFirstValue(ObjectIdClaim)
            ?? principal.FindFirstValue(LegacyObjectIdClaim)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            // A token with no stable subject cannot be attributed, and an unattributable action
            // cannot be audited. Refusing is the only correct answer (Principle V).
            return null;
        }

        return new CallerIdentity
        {
            UserId = userId,
            DisplayName = principal.FindFirstValue("name")
                ?? principal.FindFirstValue("preferred_username")
                ?? userId,
            TenantId = principal.FindFirstValue(TenantIdClaim) ?? string.Empty,
            ActingAgentId = ResolveActingAgent(principal, spaClientId),
        };
    }

    /// <summary>
    /// Identifies the agent when a request did not come from the SPA.
    /// </summary>
    /// <remarks>
    /// The authorized party claim names the client application that obtained the token. When that
    /// is anything other than the SPA, a non-interactive client is driving the request and the
    /// audit trail should say so (FR-049, SC-015).
    /// <para>
    /// This never widens what the caller may do. Under On-Behalf-Of the API sees the user's own
    /// identity and applies its ordinary authorization path; this value exists purely so that
    /// attribution and the audit trail can record who was at the keyboard, or that nobody was.
    /// </para>
    /// <para>
    /// With no SPA client id configured, every request is treated as direct rather than guessed
    /// at. Marking real user actions as agent-initiated would corrupt the audit trail in the
    /// direction that is hardest to notice.
    /// </para>
    /// </remarks>
    private static string? ResolveActingAgent(ClaimsPrincipal principal, string? spaClientId)
    {
        // An explicit actor claim, if an upstream exchange supplied one, always wins.
        var actor = principal.FindFirstValue("act_agt");
        if (!string.IsNullOrWhiteSpace(actor))
        {
            return actor;
        }

        if (string.IsNullOrWhiteSpace(spaClientId))
        {
            return null;
        }

        var authorizedParty = principal.FindFirstValue(AzpClaim) ?? principal.FindFirstValue(AppIdClaim);
        if (string.IsNullOrWhiteSpace(authorizedParty))
        {
            return null;
        }

        return string.Equals(authorizedParty, spaClientId, StringComparison.OrdinalIgnoreCase)
            ? null
            : authorizedParty;
    }
}
