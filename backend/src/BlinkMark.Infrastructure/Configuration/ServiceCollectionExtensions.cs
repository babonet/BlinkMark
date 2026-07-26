using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Storage.Blobs;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Queues;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Anchoring;
using BlinkMark.Core.Comments;
using BlinkMark.Core.Export;
using BlinkMark.Core.Preview;
using BlinkMark.Core.Quotas;
using BlinkMark.Core.Rendering;
using BlinkMark.Core.Retention;
using BlinkMark.Core.Upload;
using BlinkMark.Infrastructure.Audit;
using BlinkMark.Infrastructure.Blob;
using BlinkMark.Infrastructure.Cosmos;
using BlinkMark.Infrastructure.Crypto;
using BlinkMark.Infrastructure.Queues;
using BlinkMark.Infrastructure.Redis;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BlinkMark.Infrastructure.Configuration;

/// <summary>
/// Wires every adapter (T024).
/// </summary>
/// <remarks>
/// Read this file as the practical statement of Principle VII. Every client below is constructed
/// from an endpoint plus <see cref="DefaultAzureCredential"/>. There is no
/// <c>GetConnectionString</c>, no account key, and no client secret anywhere in the graph — and
/// nothing would work if there were, because every resource is provisioned with local
/// authentication disabled.
/// </remarks>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBlinkMarkInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BlinkMarkOptions>(configuration.GetSection(BlinkMarkOptions.SectionName));

        services.TryAddSingletonCredential();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<RetentionRules>();

        AddDomainServices(services);
        AddCosmos(services);
        AddBlobStorage(services);
        AddAuditStorage(services);
        AddQueueStorage(services);
        AddRedis(services);
        AddKeyVault(services);
        AddPreviewTokens(services);

        return services;
    }

    /// <summary>
    /// The one credential the whole application uses.
    /// </summary>
    /// <remarks>
    /// <c>ManagedIdentityClientId</c> comes from <c>AZURE_CLIENT_ID</c>, which
    /// container-apps.bicep sets to the user-assigned identity. Locally, the same call falls
    /// through to the developer's Azure CLI or Visual Studio sign-in, so nobody ever needs a
    /// credential file to run the application.
    /// </remarks>
    private static IServiceCollection TryAddSingletonCredential(this IServiceCollection services)
    {
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential(
            new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"),
                ExcludeInteractiveBrowserCredential = true,
            }));

        return services;
    }

    /// <summary>
    /// Domain services.
    /// </summary>
    /// <remarks>
    /// The renderer, sanitizer, and validator are stateless and thread-safe, so they are
    /// singletons. That is not only an efficiency choice: a single sanitizer instance means there
    /// is exactly one allowlist in the process, and no code path can construct a differently
    /// configured one.
    /// </remarks>
    private static void AddDomainServices(IServiceCollection services)
    {
        services.AddSingleton<MarkdownRenderer>();
        services.AddSingleton<HtmlSanitizerService>();
        services.AddSingleton<RenderPipeline>();
        services.AddSingleton<ExpiryGuard>();
        services.AddSingleton<AnchorService>();
        services.AddSingleton<OrphanDetector>();
        services.AddSingleton<ThreadService>();
        services.AddSingleton<RetentionService>();
        services.AddSingleton<DownloadBundleBuilder>();

        services.AddSingleton(provider =>
        {
            var upload = provider.GetRequiredService<IOptions<BlinkMarkOptions>>().Value.Upload;
            return new UploadValidator(upload.MaxSizeBytes);
        });

        services.AddSingleton(provider =>
        {
            var quotas = provider.GetRequiredService<IOptions<BlinkMarkOptions>>().Value.Quotas;
            return new QuotaSettings
            {
                MaxLiveFilesPerUser = quotas.MaxLiveFilesPerUser,
                MaxUploadsPerWindow = quotas.MaxUploadsPerWindow,
                UploadWindow = quotas.UploadWindow,
                MaxAgentRequestsPerWindow = quotas.MaxAgentRequestsPerWindow,
                AgentWindow = quotas.AgentWindow,
            };
        });

        services.AddSingleton<QuotaService>();
    }

    private static void AddCosmos(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<BlinkMarkOptions>>().Value;
            var credential = provider.GetRequiredService<TokenCredential>();

            return new CosmosClient(
                options.Cosmos.Endpoint,
                credential,
                new CosmosClientOptions
                {
                    // Direct mode over TCP is materially faster for point reads, which is what
                    // the preview path does and what SC-002's budget is spent on.
                    ConnectionMode = ConnectionMode.Direct,
                    SerializerOptions = new CosmosSerializationOptions
                    {
                        PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
                    },
                });
        });

        services.AddSingleton<CosmosContext>();
        services.AddSingleton<IFileRepository, FileRepository>();
        services.AddSingleton<CommentRepository>();
        services.AddSingleton<ICommentRepository>(provider => provider.GetRequiredService<CommentRepository>());
        services.AddSingleton<IUserPreferencesRepository, UserPreferencesRepository>();
        services.AddSingleton<INotificationRepository, NotificationRepository>();
    }

    private static void AddBlobStorage(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<BlinkMarkOptions>>().Value;
            return new BlobServiceClient(
                new Uri(options.Blob.ServiceUri),
                provider.GetRequiredService<TokenCredential>());
        });

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<BlinkMarkOptions>>().Value;
            // The Data Lake endpoint on the same account. Set Blob Expiry is only reachable
            // through this client, and only on a hierarchical-namespace account (research.md R6).
            var dfsUri = options.Blob.ServiceUri.Replace(".blob.", ".dfs.", StringComparison.Ordinal);
            return new DataLakeServiceClient(new Uri(dfsUri), provider.GetRequiredService<TokenCredential>());
        });

        services.AddSingleton<IBlobFileStore, BlobFileStore>();
    }

    private static void AddAuditStorage(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<BlinkMarkOptions>>().Value;
            return new TableServiceClient(
                new Uri(options.Audit.TableServiceUri),
                provider.GetRequiredService<TokenCredential>());
        });

        services.AddSingleton<IAuditStore, TableAuditStore>();
    }

    private static void AddQueueStorage(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<BlinkMarkOptions>>().Value;
            return new QueueServiceClient(
                new Uri(options.Notifications.QueueServiceUri),
                provider.GetRequiredService<TokenCredential>(),
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        });

        services.AddSingleton<INotificationQueue, StorageNotificationQueue>();
    }

    private static void AddRedis(IServiceCollection services)
    {
        services.AddSingleton<RedisConnectionProvider>();
        services.AddSingleton<IRateLimitStore, RedisStore>();
        services.AddSingleton<IPresenceStore, PresenceStore>();
    }

    private static void AddKeyVault(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<BlinkMarkOptions>>().Value;
            return new KeyClient(
                new Uri(options.KeyVault.Uri),
                provider.GetRequiredService<TokenCredential>());
        });

        services.AddSingleton<ITokenSigner, KeyVaultSigner>();
    }

    private static void AddPreviewTokens(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var preview = provider.GetRequiredService<IOptions<BlinkMarkOptions>>().Value.Preview;

            return new PreviewTokenOptions
            {
                Issuer = preview.IssuerOrigin,
                Audience = preview.Origin,
                // Capped inside PreviewTokenService as well. Configuration can shorten the
                // lifetime and cannot lengthen it past 15 minutes.
                Lifetime = preview.TokenLifetime,
            };
        });

        services.AddSingleton<PreviewTokenService>();
    }
}
