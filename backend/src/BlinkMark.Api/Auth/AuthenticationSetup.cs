using BlinkMark.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BlinkMark.Api.Auth;

/// <summary>
/// Entra ID token validation (T020, T136).
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠ Interim implementation. The SFI target is MISE v2 (T137).</b> SFI KPI ID-2.1.x requires
/// inbound token validation to go through Microsoft Identity Service Essentials v2
/// (<c>Microsoft.Identity.ServiceEssentials.AspNetCore</c>), and the MISE migration guidance names
/// this exact shape — <c>AddJwtBearer</c> with hand-written <see cref="TokenValidationParameters"/>
/// — as the legacy pattern to move off. The package lives on an internal feed that this
/// repository is not yet configured for, so the hardening below is a stopgap that closes the
/// obvious gaps rather than the finished answer. Do not treat it as compliant.
/// </para>
/// <para>
/// The SFI Wave deadline for MISE v2 was April 2026 and has passed. The KPI is driven by eSTS
/// telemetry keyed on application id, so an S360 action item will open against this service as
/// soon as its registration begins validating tokens — which makes the migration a launch
/// prerequisite rather than follow-up work.
/// </para>
/// <para>
/// The gap is not only procedural. MISE v2 is the only route to real-time token revocation and
/// Continuous Access Evaluation; without it a compromised token stays valid until it expires and
/// Conditional Access is enforced only at issuance. For a product whose premise is that content
/// is short-lived and reachable only inside one tenant, a token nobody can kill undercuts both
/// claims.
/// </para>
/// <para>
/// Under MISE most of this becomes declarative configuration in <c>miseconfig.json</c>. In
/// particular <c>Protocols.Bearer.TokenTypes.AccessToken</c> with <c>AppToken: false</c> and
/// <c>UserToken: true</c> expresses FR-055 — no standing agent identity, nothing acts for a
/// signed-out user — as a platform setting rather than as the claim check written below.
/// </para>
/// <para>
/// One MISE behaviour to carry into any migration: its authentication handler does not block
/// requests on its own. Enforcement lives in the authorization policy, so every policy must
/// include <c>RequireAuthenticatedUser()</c> or requests bypass authentication entirely. The
/// policies in <see cref="AuthorizationPolicies"/> already do.
/// </para>
/// <para>
/// Principle I is not satisfied by "the caller is authenticated". It requires the caller to be
/// authenticated <em>and</em> a member of the organization that owns the deployment, holding a
/// token this API issued scopes for, obtained by a client we recognise.
/// </para>
/// <para>
/// The settings below are the token-validation baseline, and none of them is a framework
/// default. Written out rather than assumed, because every one of them is a real bypass if it is
/// missing:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Issuer pinned to one tenant.</b> A token from any other tenant is rejected by the handler
/// before a line of application code runs — a platform property rather than a claim check
/// someone can forget to write.
/// </description></item>
/// <item><description>
/// <b>Audience pinned to this API</b>, and <c>RequireAudience</c> so a token with no audience at
/// all cannot slip through.
/// </description></item>
/// <item><description>
/// <b>Algorithm allow-list.</b> Without <c>ValidAlgorithms</c>, the handler will verify with
/// whatever the token's own header asked for. Algorithm confusion is the oldest JWT bug there is
/// and the fix is to state what we are willing to accept.
/// </description></item>
/// <item><description>
/// <b>Token type allow-list.</b> <c>ValidTypes</c> stops an ID token — which is not an
/// authorization grant and is not audience-scoped to this API in the same way — being replayed
/// as an access token.
/// </description></item>
/// <item><description>
/// <b>App-only tokens refused outright.</b> A token with no delegated scope is an application
/// acting for itself. FR-055 forbids that. Enforcing it at the token layer means it holds for
/// every endpoint, including ones not written yet.
/// </description></item>
/// <item><description>
/// <b>Authorized-party allow-list.</b> Any client in the tenant can request a token for a
/// resource its user consents to. Checking <c>azp</c> means only clients we have approved can
/// reach this API at all.
/// </description></item>
/// <item><description>
/// <b>The token is never persisted.</b> <c>SaveToken = false</c> keeps the raw bearer value out
/// of authentication properties, where it would otherwise be one careless serialization away
/// from a log.
/// </description></item>
/// </list>
/// </remarks>
public static class AuthenticationSetup
{
    public const string EntraScheme = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>Delegated scopes this API publishes. A token must carry at least one.</summary>
    public static readonly string[] KnownScopes = ["Files.ReadWrite", "Comments.ReadWrite"];

    public static IServiceCollection AddBlinkMarkAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var entra = configuration
            .GetSection($"{BlinkMarkOptions.SectionName}:Entra")
            .Get<EntraOptions>() ?? new EntraOptions();

        var instance = entra.Instance.TrimEnd('/');
        var authority = $"{instance}/{entra.TenantId}/v2.0";

        services
            .AddAuthentication(EntraScheme)
            .AddJwtBearer(EntraScheme, options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = true;

                // The raw bearer value has no business outliving the request that carried it.
                options.SaveToken = false;
                options.MapInboundClaims = false;

                // Signing keys rotate. Refreshing on a schedule means a rotation is invisible
                // rather than an outage, and the shorter refresh-on-failure interval means a
                // rotation we did not anticipate costs seconds instead of the default hour.
                options.AutomaticRefreshInterval = TimeSpan.FromHours(12);
                options.RefreshInterval = TimeSpan.FromMinutes(5);

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // Single tenant, two accepted issuer spellings because Entra emits the v2.0
                    // form and some tooling still emits the v1.0 form for the same tenant.
                    ValidateIssuer = true,
                    ValidIssuers =
                    [
                        $"{instance}/{entra.TenantId}/v2.0",
                        $"https://sts.windows.net/{entra.TenantId}/",
                    ],

                    ValidateAudience = true,
                    RequireAudience = true,
                    ValidAudiences = string.IsNullOrWhiteSpace(entra.Audience)
                        ? [entra.ClientId]
                        : [entra.Audience, entra.ClientId],

                    ValidateLifetime = true,
                    RequireExpirationTime = true,

                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,

                    // Entra signs with RS256. Anything else arriving here is either a
                    // misconfiguration or an attack, and both deserve the same answer.
                    ValidAlgorithms = entra.AdditionalValidAlgorithms.Length == 0
                        ? [SecurityAlgorithms.RsaSha256]
                        : [SecurityAlgorithms.RsaSha256, .. entra.AdditionalValidAlgorithms],

                    // `at+jwt` is the RFC 9068 access-token type. `JWT` is accepted because
                    // Entra v1.0-format tokens still use it.
                    ValidTypes = ["JWT", "at+jwt"],

                    // Thirty seconds rather than the five-minute default. A retention product
                    // whose whole premise is that expiry is exact should not accept a token for
                    // five minutes after it died.
                    ClockSkew = TimeSpan.FromSeconds(30),

                    NameClaimType = "name",
                    RoleClaimType = "roles",
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context => ValidateBeyondTheHandlerAsync(context, entra),

                    OnChallenge = context =>
                    {
                        // No WWW-Authenticate detail, no error description. FR-001 requires an
                        // unauthenticated caller to learn nothing at all — not even whether the
                        // thing they asked for exists.
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    },

                    OnAuthenticationFailed = context =>
                    {
                        // The reason, never the token. A rejected bearer value is still a bearer
                        // value, and a log line is a much easier place to steal one from than a
                        // request.
                        context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("BlinkMark.Api.Authentication")
                            .LogInformation(
                                "Token rejected on {Method} {Path}: {Reason}.",
                                context.HttpContext.Request.Method,
                                context.HttpContext.Request.Path,
                                context.Exception.GetType().Name);

                        return Task.CompletedTask;
                    },
                };
            });

        return services;
    }

    /// <summary>
    /// The claim checks the handler does not make for us.
    /// </summary>
    /// <remarks>
    /// Issuer, audience, signature, and lifetime are the handler's job. Tenant, token version,
    /// delegation, and authorized party are ours — and each of them is a way a structurally valid
    /// token could still be the wrong token.
    /// <para>
    /// A failure here calls <c>Fail</c> rather than throwing, so the caller receives the same
    /// blank 401 as any other refusal and learns nothing about which check tripped.
    /// </para>
    /// </remarks>
    private static Task ValidateBeyondTheHandlerAsync(TokenValidatedContext context, EntraOptions entra)
    {
        var principal = context.Principal;
        if (principal is null)
        {
            context.Fail("No principal.");
            return Task.CompletedTask;
        }

        // Tenant, again. The issuer check already covers this, and stating it twice costs
        // nothing — the cost of being wrong is a cross-tenant read of somebody's draft.
        var tenantId = principal.FindFirst("tid")?.Value;
        if (!string.IsNullOrWhiteSpace(entra.TenantId)
            && !string.Equals(tenantId, entra.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            context.Fail("Token was issued for a different tenant.");
            return Task.CompletedTask;
        }

        // v1.0 tokens carry different claim shapes for the same facts. Requiring v2.0 means the
        // claim handling below has exactly one format to reason about.
        var version = principal.FindFirst("ver")?.Value;
        if (!string.IsNullOrWhiteSpace(version) && version != "2.0")
        {
            context.Fail("Only v2.0 access tokens are accepted.");
            return Task.CompletedTask;
        }

        // FR-055, enforced at the token layer. `scp` is present on delegated tokens and absent on
        // app-only ones, so "no scope" means an application is acting for itself — which this
        // product does not permit under any circumstances, agent or otherwise.
        var scopes = principal.FindFirst("scp")?.Value;
        if (string.IsNullOrWhiteSpace(scopes))
        {
            context.Fail("App-only tokens are not accepted; every action is on behalf of a signed-in user.");
            return Task.CompletedTask;
        }

        var granted = scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!granted.Intersect(KnownScopes, StringComparer.Ordinal).Any())
        {
            context.Fail("The token carries none of this API's scopes.");
            return Task.CompletedTask;
        }

        // Any client in the tenant can ask for a token to a resource its user consents to.
        // Without this, an unrelated internal application could read BlinkMark drafts simply by
        // asking its users for consent.
        if (entra.AllowedClientIds.Length > 0)
        {
            var authorizedParty = principal.FindFirst("azp")?.Value ?? principal.FindFirst("appid")?.Value;

            if (string.IsNullOrWhiteSpace(authorizedParty)
                || !entra.AllowedClientIds.Contains(authorizedParty, StringComparer.OrdinalIgnoreCase))
            {
                context.Fail("The token was obtained by a client that is not approved for this API.");
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }
}
