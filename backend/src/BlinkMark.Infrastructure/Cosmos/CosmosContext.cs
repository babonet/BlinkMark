using BlinkMark.Infrastructure.Configuration;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace BlinkMark.Infrastructure.Cosmos;

/// <summary>
/// Holds the container handles the repositories use.
/// </summary>
/// <remarks>
/// A single <see cref="CosmosClient"/> per process, as the SDK requires. Containers are resolved
/// once rather than per call, because <c>GetContainer</c> is cheap but not free and the preview
/// path has a one-second budget to defend.
/// </remarks>
public sealed class CosmosContext
{
    public CosmosContext(CosmosClient client, IOptions<BlinkMarkOptions> options)
    {
        var cosmos = options.Value.Cosmos;

        Files = client.GetContainer(cosmos.Database, cosmos.FilesContainer);
        Comments = client.GetContainer(cosmos.Database, cosmos.CommentsContainer);
        Notifications = client.GetContainer(cosmos.Database, cosmos.NotificationsContainer);
        UserPreferences = client.GetContainer(cosmos.Database, cosmos.UserPreferencesContainer);
    }

    /// <summary>Partition key <c>/id</c> — point read on the preview hot path.</summary>
    public Container Files { get; }

    /// <summary>Partition key <c>/fileId</c> — single-partition list per file.</summary>
    public Container Comments { get; }

    /// <summary>Partition key <c>/recipientId</c>.</summary>
    public Container Notifications { get; }

    /// <summary>Partition key <c>/id</c>, no TTL.</summary>
    public Container UserPreferences { get; }
}
