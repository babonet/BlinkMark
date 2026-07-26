namespace BlinkMark.Core.Models;

/// <summary>Auditable actions (FR-041).</summary>
/// <remarks>
/// <c>View</c> and <c>Preview</c> are separate because they happen in different services and
/// mean different things. <c>View</c> is a metadata read at the API; <c>Preview</c> is an actual
/// content fetch at the isolated preview origin. Only the preview host knows whether an issued
/// token was ever redeemed, so only it can write the second entry accurately.
/// </remarks>
public enum AuditAction
{
    Upload,
    View,
    Preview,
    Download,
    CommentCreate,
    CommentEdit,
    CommentDelete,
    RetentionExtend,
    FileDelete,
    FileExpire,
}

/// <summary>How an audited action ended.</summary>
/// <remarks>
/// Denials are audited too. An audit trail that records only successes answers "what happened"
/// but not "what was attempted", and the second question is the one an investigation asks.
/// </remarks>
public enum AuditOutcome
{
    Success,
    Denied,
    Error,
}

/// <summary>What an audit entry is about.</summary>
public enum AuditTargetType
{
    File,
    Comment,
}

/// <summary>
/// One immutable line in the audit trail. Table Storage, dedicated account.
/// </summary>
/// <remarks>
/// Append-only by application contract (research.md R8). The repository interface exposes only
/// an append operation, the account carries a resource lock, and a test asserts no delete or
/// merge path exists in the compiled application.
/// <para>
/// Worth being honest about: Azure Storage immutability policies apply to blob containers, not
/// to tables. There is no platform-enforced write-once guarantee here. FR-044 asks for no
/// <em>interface</em> that permits modification, which is satisfied literally — but
/// application-enforced and platform-enforced are different claims and only one of them is true.
/// </para>
/// <para>
/// Never contains file content, comment text, or credentials (FR-045).
/// </para>
/// </remarks>
public sealed record AuditEntry
{
    /// <summary><c>yyyyMMdd</c>. Bounds partition growth and matches how operators query.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Reverse tick plus ULID, so the newest entry in a day sorts first.</summary>
    public required string RowKey { get; init; }

    public required string ActorId { get; init; }

    public required string ActorDisplayName { get; init; }

    /// <summary>
    /// Null for a direct user action; set when an agent acted.
    /// </summary>
    /// <remarks>
    /// This single nullable field is what makes FR-049 and SC-015 answerable. Without it, "did a
    /// person do this or did their agent?" has no answer after the fact.
    /// </remarks>
    public string? ActingAgentId { get; init; }

    public required AuditAction Action { get; init; }

    public required AuditTargetType TargetType { get; init; }

    public required string TargetId { get; init; }

    public required AuditOutcome Outcome { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Propagated frontend to API to storage (Principle V).</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Retention changes only (FR-030).</summary>
    public DateTimeOffset? PreviousExpiresAt { get; init; }

    /// <summary>Retention changes only.</summary>
    public DateTimeOffset? NewExpiresAt { get; init; }

    /// <summary>
    /// Builds an entry with the partition and row keys derived from <paramref name="occurredAt"/>.
    /// </summary>
    public static AuditEntry Create(
        AuditAction action,
        AuditTargetType targetType,
        string targetId,
        string actorId,
        string actorDisplayName,
        string correlationId,
        DateTimeOffset occurredAt,
        AuditOutcome outcome = AuditOutcome.Success,
        string? actingAgentId = null,
        DateTimeOffset? previousExpiresAt = null,
        DateTimeOffset? newExpiresAt = null)
    {
        var utc = occurredAt.ToUniversalTime();

        return new AuditEntry
        {
            PartitionKey = utc.ToString("yyyyMMdd"),
            // Subtracting from MaxValue makes Table Storage's ascending row-key order produce
            // newest-first reads, which is the only order an operator ever wants.
            RowKey = $"{DateTime.MaxValue.Ticks - utc.UtcDateTime.Ticks:D19}-{Identifiers.New(utc)}",
            ActorId = actorId,
            ActorDisplayName = actorDisplayName,
            ActingAgentId = actingAgentId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Outcome = outcome,
            OccurredAt = utc,
            CorrelationId = correlationId,
            PreviousExpiresAt = previousExpiresAt,
            NewExpiresAt = newExpiresAt,
        };
    }
}
