namespace BlinkMark.Infrastructure.Configuration;

/// <summary>
/// Everything BlinkMark needs to reach its dependencies.
/// </summary>
/// <remarks>
/// Read this type as an assertion about Principle VII: every property below is an endpoint, a
/// name, or an identifier. There is no connection string, no account key, and no client secret,
/// because there is nowhere for one to go. Authentication is always the ambient managed
/// identity through <c>DefaultAzureCredential</c>.
/// </remarks>
public sealed class BlinkMarkOptions
{
    public const string SectionName = "BlinkMark";

    public CosmosOptions Cosmos { get; set; } = new();

    public BlobOptions Blob { get; set; } = new();

    public AuditOptions Audit { get; set; } = new();

    public NotificationOptions Notifications { get; set; } = new();

    public RedisOptions Redis { get; set; } = new();

    public KeyVaultOptions KeyVault { get; set; } = new();

    public PreviewOptions Preview { get; set; } = new();

    public EntraOptions Entra { get; set; } = new();

    public UploadOptions Upload { get; set; } = new();

    public QuotaOptions Quotas { get; set; } = new();

    public PresenceOptions Presence { get; set; } = new();
}

public sealed class CosmosOptions
{
    /// <summary>Account endpoint. Local auth is disabled on the account, so this is not a secret.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string Database { get; set; } = "blinkmark";

    public string FilesContainer { get; set; } = "files";

    public string CommentsContainer { get; set; } = "comments";

    public string NotificationsContainer { get; set; } = "notifications";

    public string UserPreferencesContainer { get; set; } = "userPrefs";
}

public sealed class BlobOptions
{
    public string ServiceUri { get; set; } = string.Empty;

    public string OriginalsContainer { get; set; } = "originals";

    public string RendersContainer { get; set; } = "renders";

    public string ProjectionsContainer { get; set; } = "projections";
}

public sealed class AuditOptions
{
    public string TableServiceUri { get; set; } = string.Empty;

    public string TableName { get; set; } = "AuditEntry";
}

public sealed class NotificationOptions
{
    public string QueueServiceUri { get; set; } = string.Empty;

    public string QueueName { get; set; } = "notifications";

    /// <summary>How long comment events are held before one notification is raised (FR-038).</summary>
    public TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class RedisOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 6380;

    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Set only for local development against the emulator in docker-compose.
    /// </summary>
    /// <remarks>
    /// In every deployed environment this is empty and authentication is a managed-identity
    /// token, because the cache has <c>disableAccessKeyAuthentication: true</c> and would refuse
    /// anything else.
    /// </remarks>
    public bool UseEntraAuthentication { get; set; } = true;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class KeyVaultOptions
{
    public string Uri { get; set; } = string.Empty;
}

public sealed class PreviewOptions
{
    /// <summary>Origin of the preview host, for example <c>https://preview.example.com</c>.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>Origin of the API, which issues preview tokens.</summary>
    public string IssuerOrigin { get; set; } = string.Empty;

    /// <summary>Name of the Key Vault key used to sign preview tokens.</summary>
    public string SigningKeyName { get; set; } = "preview-token-signing";

    /// <summary>The SPA origin allowed to frame preview content.</summary>
    public string FrameAncestor { get; set; } = string.Empty;

    /// <summary>Capped at 15 minutes by <c>PreviewTokenService.MaximumLifetime</c>.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed class EntraOptions
{
    /// <summary>The single tenant that owns the application (Principle I).</summary>
    public string TenantId { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>
    /// Application id of the SPA.
    /// </summary>
    /// <remarks>
    /// Used to tell a direct user action from an agent-initiated one: a token whose authorized
    /// party is the SPA came from a person clicking something, and anything else did not
    /// (FR-048, FR-049).
    /// </remarks>
    public string SpaClientId { get; set; } = string.Empty;

    /// <summary>
    /// Client applications permitted to obtain a token for this API.
    /// </summary>
    /// <remarks>
    /// Any application in the tenant can request a token for a resource its user consents to, so
    /// without this list an unrelated internal app could read BlinkMark drafts simply by asking
    /// its own users for consent. Empty disables the check, which is intended only for local
    /// development — every deployed environment sets it.
    /// </remarks>
    public string[] AllowedClientIds { get; set; } = [];

    /// <summary>
    /// Extra signing algorithms accepted alongside RS256.
    /// </summary>
    /// <remarks>
    /// Exists so the in-process test host can present symmetrically signed tokens through the
    /// real validation path. Never set in a deployed environment; the SFI gate would be within
    /// its rights to fail the build if it were.
    /// </remarks>
    public string[] AdditionalValidAlgorithms { get; set; } = [];

    /// <summary>
    /// Service Tree id of the owning service.
    /// </summary>
    /// <remarks>
    /// Carried in configuration so the running application can report which registered service it
    /// belongs to. Ownership metadata is not decoration: an application nobody owns is an
    /// application nobody patches.
    /// </remarks>
    public string ServiceTreeId { get; set; } = string.Empty;
}

public sealed class UploadOptions
{
    /// <summary>FR-007.</summary>
    public long MaxSizeBytes { get; set; } = 20 * 1024 * 1024;

    public string[] AllowedExtensions { get; set; } = [".html", ".htm", ".md", ".markdown"];
}

public sealed class QuotaOptions
{
    /// <summary>FR-083. Live files per user.</summary>
    public int MaxLiveFilesPerUser { get; set; } = 50;

    /// <summary>FR-085. Uploads per user per window.</summary>
    public int MaxUploadsPerWindow { get; set; } = 20;

    public TimeSpan UploadWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>FR-054. Agent requests per represented user per window.</summary>
    public int MaxAgentRequestsPerWindow { get; set; } = 120;

    public TimeSpan AgentWindow { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class PresenceOptions
{
    /// <summary>
    /// Per-viewer key lifetime. Expiry is the departure mechanism (FR-062).
    /// </summary>
    public TimeSpan ViewerTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the client refreshes its key. Comfortably inside the TTL.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>FR-066. Viewers named individually before the display switches to a count.</summary>
    public int MaxNamedViewers { get; set; } = 8;
}
