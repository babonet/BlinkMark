using BlinkMark.Core.Abstractions;
using BlinkMark.TestHost.Fakes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace BlinkMark.TestHost;

/// <summary>
/// Hosts the API in-process with in-memory adapters (T028).
/// </summary>
/// <remarks>
/// Azure clients are replaced; authentication, authorization, routing, model binding, error
/// handling, and every line of application logic are the real ones. That boundary is chosen
/// carefully — Principle VI's mandatory tests are about authorization, retention, anchoring, and
/// sanitization, none of which live in the storage SDK, and all of which live in code that runs
/// unchanged here.
/// </remarks>
public sealed class BlinkMarkApiFactory : WebApplicationFactory<BlinkMark.Api.ApiHostMarker>
{
    public const string TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    public const string OtherTenantId = "11111111-2222-3333-4444-555555555555";
    public const string ApiAudience = "api://blinkmark-test";
    public const string SpaClientId = "blinkmark-test-spa";
    public const string ApprovedAgentClientId = "blinkmark-test-agent";
    public const string PreviewOrigin = "https://preview.blinkmark.test";
    public const string ApiOrigin = "https://api.blinkmark.test";

    public TestClock Clock { get; } = new();

    public TestTokenIssuer Tokens { get; } = new(TenantId, ApiAudience, SpaClientId);

    public InMemoryFileRepository Files { get; private set; } = null!;

    public InMemoryCommentRepository Comments { get; private set; } = null!;

    public InMemoryBlobFileStore Blobs { get; private set; } = null!;

    public InMemoryAuditStore Audit { get; private set; } = null!;

    public InMemoryRateLimitStore RateLimits { get; private set; } = null!;

    public InMemoryPresenceStore Presence { get; private set; } = null!;

    public InMemoryNotificationQueue NotificationQueue { get; private set; } = null!;

    public TestTokenSigner Signer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.UseSetting("BlinkMark:Entra:TenantId", TenantId);
        builder.UseSetting("BlinkMark:Entra:ClientId", "blinkmark-test-client");
        builder.UseSetting("BlinkMark:Entra:Audience", ApiAudience);
        builder.UseSetting("BlinkMark:Entra:SpaClientId", SpaClientId);
        builder.UseSetting("BlinkMark:Entra:AllowedClientIds:0", SpaClientId);
        builder.UseSetting("BlinkMark:Entra:AllowedClientIds:1", ApprovedAgentClientId);
        // The in-process host presents symmetrically signed tokens. Production accepts RS256
        // only; this is the single relaxation, and it is scoped to the test host.
        builder.UseSetting("BlinkMark:Entra:AdditionalValidAlgorithms:0", "HS256");
        builder.UseSetting("BlinkMark:Preview:Origin", PreviewOrigin);
        builder.UseSetting("BlinkMark:Preview:IssuerOrigin", ApiOrigin);
        builder.UseSetting("BlinkMark:Preview:FrameAncestor", "https://app.blinkmark.test");
        builder.UseSetting("BlinkMark:Cosmos:Endpoint", "https://cosmos.invalid/");
        builder.UseSetting("BlinkMark:Blob:ServiceUri", "https://blob.invalid/");
        builder.UseSetting("BlinkMark:Audit:TableServiceUri", "https://table.invalid/");
        builder.UseSetting("BlinkMark:Notifications:QueueServiceUri", "https://queue.invalid/");
        builder.UseSetting("BlinkMark:KeyVault:Uri", "https://vault.invalid/");

        builder.ConfigureTestServices(services =>
        {
            Files = new InMemoryFileRepository(Clock);
            Comments = new InMemoryCommentRepository(Clock);
            Blobs = new InMemoryBlobFileStore(Clock);
            Audit = new InMemoryAuditStore();
            RateLimits = new InMemoryRateLimitStore(Clock);
            Presence = new InMemoryPresenceStore(Clock);
            NotificationQueue = new InMemoryNotificationQueue();

            services.Replace(ServiceDescriptor.Singleton<IClock>(Clock));
            services.Replace(ServiceDescriptor.Singleton<IFileRepository>(Files));
            services.Replace(ServiceDescriptor.Singleton<ICommentRepository>(Comments));
            services.Replace(ServiceDescriptor.Singleton<IBlobFileStore>(Blobs));
            services.Replace(ServiceDescriptor.Singleton<IAuditStore>(Audit));
            services.Replace(ServiceDescriptor.Singleton<IRateLimitStore>(RateLimits));
            services.Replace(ServiceDescriptor.Singleton<IPresenceStore>(Presence));
            services.Replace(ServiceDescriptor.Singleton<INotificationQueue>(NotificationQueue));
            services.Replace(ServiceDescriptor.Singleton<IUserPreferencesRepository>(
                new InMemoryUserPreferencesRepository()));
            services.Replace(ServiceDescriptor.Singleton<INotificationRepository>(
                new InMemoryNotificationRepository()));
            services.Replace(ServiceDescriptor.Singleton<ITokenSigner>(Signer));

            services.ConfigureTestJwtBearer();
        });
    }
}

/// <summary>Shared JwtBearer wiring for the in-process hosts.</summary>
public static class TestAuthenticationExtensions
{
    /// <summary>
    /// Points the real JwtBearer handler at a symmetric test key.
    /// </summary>
    /// <remarks>
    /// Only the signing key and the metadata source change. Issuer pinning, audience pinning, and
    /// lifetime validation all still run, which is what lets a cross-tenant or expired-token test
    /// prove something about production behaviour rather than about the test harness.
    /// </remarks>
    public static IServiceCollection ConfigureTestJwtBearer(this IServiceCollection services)
    {
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.RequireHttpsMetadata = false;

            // An empty static configuration stops the handler from reaching out to an OpenID
            // Connect metadata endpoint that does not exist in a test run.
            options.Configuration = new OpenIdConnectConfiguration();

            options.TokenValidationParameters.IssuerSigningKey = TestTokenIssuer.SigningKey;
            options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        });

        return services;
    }
}
