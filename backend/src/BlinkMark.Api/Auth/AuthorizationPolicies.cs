using System.Security.Claims;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;
using Microsoft.AspNetCore.Authorization;

namespace BlinkMark.Api.Auth;

/// <summary>
/// Authorization policies (T021).
/// </summary>
/// <remarks>
/// Three levels, and the gap between the second and third is the interesting one.
/// <list type="bullet">
/// <item><description>
/// <b>Tenant member</b> — an authenticated member of the owning organization. Enforced at token
/// validation; the policy exists so an endpoint has to opt in rather than inherit it silently.
/// </description></item>
/// <item><description>
/// <b>File viewer</b> — clarification Q1 answered this: any authenticated organization member
/// holding the link may view and comment. The link <em>is</em> the access grant. That is a real
/// residual risk, mitigated by disclosure at upload time (FR-057, SC-016) rather than by an
/// access list, and it is recorded as accepted rather than overlooked.
/// </description></item>
/// <item><description>
/// <b>File owner</b> — only the uploader may extend retention, delete early, or download the
/// bundle (FR-029, FR-058, FR-072).
/// </description></item>
/// </list>
/// <para>
/// Ownership depends on the file, so it cannot be a claims-only policy. It is a requirement with
/// a handler that loads the file, which also means expiry is checked on the same path.
/// </para>
/// </remarks>
public static class AuthorizationPolicies
{
    public const string TenantMember = "TenantMember";
    public const string FileViewer = "FileViewer";
    public const string FileOwner = "FileOwner";

    public static IServiceCollection AddBlinkMarkAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, FileOwnerHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(TenantMember, policy => policy
                .RequireAuthenticatedUser()
                .AddAuthenticationSchemes(AuthenticationSetup.EntraScheme)
                .RequireAssertion(context => context.User.GetCaller() is not null))
            .AddPolicy(FileViewer, policy => policy
                .RequireAuthenticatedUser()
                .AddAuthenticationSchemes(AuthenticationSetup.EntraScheme)
                .RequireAssertion(context => context.User.GetCaller() is not null))
            .AddPolicy(FileOwner, policy => policy
                .RequireAuthenticatedUser()
                .AddAuthenticationSchemes(AuthenticationSetup.EntraScheme)
                .AddRequirements(new FileOwnerRequirement()));

        // Nothing is reachable without an explicit policy. An endpoint that forgets to declare
        // one gets the fallback, not anonymous access.
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = options.GetPolicy(TenantMember);
        });

        return services;
    }
}

/// <summary>Requires the caller to own the file named in the route.</summary>
public sealed class FileOwnerRequirement : IAuthorizationRequirement;

/// <summary>
/// Resolves ownership by loading the file.
/// </summary>
/// <remarks>
/// Expiry is checked here as well as in the read path. An expired file has no owner for
/// authorization purposes, because it reads as gone to everyone (FR-032) — and a rule enforced
/// in one place is a rule that a second code path can miss.
/// </remarks>
public sealed class FileOwnerHandler(
    IFileRepository files,
    IClock clock,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<FileOwnerRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FileOwnerRequirement requirement)
    {
        var caller = context.User.GetCaller();
        if (caller is null)
        {
            return;
        }

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }

        var fileId = httpContext.Request.RouteValues.TryGetValue("fileId", out var raw)
            ? raw?.ToString()
            : null;

        if (!Identifiers.IsValid(fileId))
        {
            return;
        }

        var file = await files.GetAsync(fileId!, httpContext.RequestAborted).ConfigureAwait(false);
        if (file is null || file.IsExpired(clock.UtcNow))
        {
            return;
        }

        if (string.Equals(file.OwnerId, caller.UserId, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
    }
}
