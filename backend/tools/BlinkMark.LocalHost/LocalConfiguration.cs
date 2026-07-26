using BlinkMark.Core.Abstractions;
using BlinkMark.TestHost;
using BlinkMark.TestHost.Fakes;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlinkMark.LocalHost;

/// <summary>
/// Settings and wiring for the local development host.
/// </summary>
public static class LocalConfiguration
{
    /// <summary>Microsoft's real tenant id, so local tokens look like the ones production sees.</summary>
    public const string TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";

    public const string ApiAudience = "api://blinkmark-local";
    public const string SpaClientId = "blinkmark-local-spa";
    public const string AgentClientId = "blinkmark-local-agent";

    /// <summary>
    /// The three origins.
    /// </summary>
    /// <remarks>
    /// `localhost` and `127.0.0.1` are genuinely different origins to a browser, despite
    /// resolving to the same machine. That is what lets the preview host be properly isolated
    /// locally without editing a hosts file or depending on external DNS — the same-origin policy
    /// separates them exactly as it separates the deployed hostnames, so a sanitizer bypass
    /// tested here lands somewhere with no access to the application's tokens (Principle IV).
    /// </remarks>
    public const string ApiOrigin = "http://localhost:5080";

    public const string PreviewOrigin = "http://127.0.0.1:5081";

    public const string SpaOrigin = "http://localhost:5173";

    public static Dictionary<string, string?> ApiSettings() => new()
    {
        ["BlinkMark:Entra:TenantId"] = TenantId,
        ["BlinkMark:Entra:ClientId"] = "blinkmark-local-client",
        ["BlinkMark:Entra:Audience"] = ApiAudience,
        ["BlinkMark:Entra:SpaClientId"] = SpaClientId,
        ["BlinkMark:Entra:AllowedClientIds:0"] = SpaClientId,
        ["BlinkMark:Entra:AllowedClientIds:1"] = AgentClientId,

        // The local issuer signs symmetrically. Production accepts RS256 only; this is the single
        // relaxation, and it exists only in this never-deployed host.
        ["BlinkMark:Entra:AdditionalValidAlgorithms:0"] = "HS256",

        ["BlinkMark:Preview:Origin"] = PreviewOrigin,
        ["BlinkMark:Preview:IssuerOrigin"] = ApiOrigin,
        ["BlinkMark:Preview:FrameAncestor"] = SpaOrigin,
        ["BlinkMark:Cors:AllowedOrigin"] = SpaOrigin,

        // Present so that options binding succeeds. Nothing dials them: every Azure client is
        // replaced before the container is built.
        ["BlinkMark:Cosmos:Endpoint"] = "https://cosmos.invalid/",
        ["BlinkMark:Blob:ServiceUri"] = "https://blob.invalid/",
        ["BlinkMark:Audit:TableServiceUri"] = "https://table.invalid/",
        ["BlinkMark:Notifications:QueueServiceUri"] = "https://queue.invalid/",
        ["BlinkMark:KeyVault:Uri"] = "https://vault.invalid/",

        // Application Insights is off; a local run should not emit telemetry anywhere.
        ["ApplicationInsights:ConnectionString"] = string.Empty,
    };

    public static Dictionary<string, string?> PreviewSettings()
    {
        var settings = ApiSettings();
        settings["BlinkMark:Preview:FrameAncestor"] = SpaOrigin;
        return settings;
    }

    /// <summary>
    /// Swaps every Azure-backed adapter for its in-memory equivalent.
    /// </summary>
    /// <remarks>
    /// These are the same fakes the 128 backend tests run against, not a second implementation
    /// written for the demo. That is the point: they enforce the retention ceiling, they expire
    /// content on read, and they refuse an out-of-range expiry, so a bug you fail to reproduce
    /// locally is a bug that is genuinely not in the application logic.
    /// <para>
    /// What they do not do is persist. Everything vanishes when the process exits, which for a
    /// product whose entire premise is short-lived content is arguably the most faithful part of
    /// the whole arrangement.
    /// </para>
    /// </remarks>
    public static IServiceCollection ReplaceWithInMemoryAdapters(
        this IServiceCollection services,
        IClock clock,
        InMemoryBlobFileStore blobs,
        InMemoryAuditStore audit,
        TestTokenSigner signer)
    {
        services.Replace(ServiceDescriptor.Singleton(clock));
        services.Replace(ServiceDescriptor.Singleton<IFileRepository>(new InMemoryFileRepository(clock)));
        services.Replace(ServiceDescriptor.Singleton<ICommentRepository>(new InMemoryCommentRepository(clock)));
        services.Replace(ServiceDescriptor.Singleton<IBlobFileStore>(blobs));
        services.Replace(ServiceDescriptor.Singleton<IAuditStore>(audit));
        services.Replace(ServiceDescriptor.Singleton<IRateLimitStore>(new InMemoryRateLimitStore(clock)));
        services.Replace(ServiceDescriptor.Singleton<IPresenceStore>(new InMemoryPresenceStore(clock)));
        services.Replace(ServiceDescriptor.Singleton<INotificationQueue>(new InMemoryNotificationQueue()));
        services.Replace(ServiceDescriptor.Singleton<IUserPreferencesRepository>(new InMemoryUserPreferencesRepository()));
        services.Replace(ServiceDescriptor.Singleton<INotificationRepository>(new InMemoryNotificationRepository()));
        services.Replace(ServiceDescriptor.Singleton<ITokenSigner>(signer));

        // The Cosmos client is registered by AddBlinkMarkInfrastructure and would try to parse an
        // endpoint on first resolve. Nothing needs it once the repositories are replaced, so it is
        // removed outright rather than pointed at a URL that does not exist.
        RemoveAll<Microsoft.Azure.Cosmos.CosmosClient>(services);
        RemoveAll<BlinkMark.Infrastructure.Cosmos.CosmosContext>(services);

        return services;
    }

    private static void RemoveAll<T>(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }

    public static void PrintBanner(TestTokenIssuer tokens)
    {
        var token = tokens.IssueUserToken("local-dev-user", "Local Developer", DateTimeOffset.UtcNow.AddHours(8));

        Console.WriteLine();
        Console.WriteLine("  BlinkMark — local development host");
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────");
        Console.WriteLine($"  API       {ApiOrigin}      (Swagger at {ApiOrigin}/swagger)");
        Console.WriteLine($"  Preview   {PreviewOrigin}   — a separate origin, on purpose");
        Console.WriteLine($"  Frontend  {SpaOrigin}   — run `npm run dev` in frontend/");
        Console.WriteLine();
        Console.WriteLine("  Storage is in memory. Everything is lost when this process exits.");
        Console.WriteLine();
        Console.WriteLine("  Authentication is REAL — these are genuine JWTs going through the");
        Console.WriteLine("  production handler. Cross-tenant, expired, and app-only tokens are");
        Console.WriteLine("  still refused. Only the signing key is local.");
        Console.WriteLine();
        Console.WriteLine($"  Get a token:  GET {ApiOrigin}/dev/token?user=alice");
        Console.WriteLine();
        Console.WriteLine("  A token for 'local-dev-user', valid 8 hours:");
        Console.WriteLine();
        Console.WriteLine($"  $env:TOKEN = \"{token}\"");
        Console.WriteLine();
    }
}
