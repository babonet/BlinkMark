using BlinkMark.Api.Auth;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;

namespace BlinkMark.Api.Middleware;

/// <summary>
/// Writes audit entries (T022, T124).
/// </summary>
/// <remarks>
/// One place, so that every entry carries the same fields and nobody has to remember which ones
/// matter. Two of them are easy to omit and both are requirements:
/// <list type="bullet">
/// <item><description>
/// <c>actingAgentId</c> on every entry written during an agent-initiated request. Without it,
/// "did a person do this, or their agent?" has no answer after the fact (FR-049, SC-015).
/// </description></item>
/// <item><description>
/// The correlation id, propagated from the edge, which is what ties an audit entry to the trace
/// that produced it (Principle V).
/// </description></item>
/// </list>
/// <para>
/// Denials are audited alongside successes. A trail that records only what worked answers "what
/// happened" but not "what was attempted", and an investigation asks the second question.
/// </para>
/// <para>
/// Nothing here ever receives file content, comment text, or a credential (FR-045). The method
/// signatures take identifiers only, so there is no parameter for a caller to put them in.
/// </para>
/// </remarks>
public sealed class AuditService(IAuditStore store, IClock clock, ILogger<AuditService> logger)
{
    private readonly IAuditStore _store = store;
    private readonly IClock _clock = clock;
    private readonly ILogger<AuditService> _logger = logger;

    public Task RecordAsync(
        HttpContext context,
        AuditAction action,
        AuditTargetType targetType,
        string targetId,
        AuditOutcome outcome = AuditOutcome.Success,
        DateTimeOffset? previousExpiresAt = null,
        DateTimeOffset? newExpiresAt = null,
        CancellationToken cancellationToken = default)
    {
        var caller = context.GetCaller();

        return RecordAsync(
            action,
            targetType,
            targetId,
            caller?.UserId ?? "anonymous",
            caller?.DisplayName ?? "anonymous",
            context.GetCorrelationId(),
            caller?.ActingAgentId,
            outcome,
            previousExpiresAt,
            newExpiresAt,
            cancellationToken);
    }

    public async Task RecordAsync(
        AuditAction action,
        AuditTargetType targetType,
        string targetId,
        string actorId,
        string actorDisplayName,
        string correlationId,
        string? actingAgentId = null,
        AuditOutcome outcome = AuditOutcome.Success,
        DateTimeOffset? previousExpiresAt = null,
        DateTimeOffset? newExpiresAt = null,
        CancellationToken cancellationToken = default)
    {
        var entry = AuditEntry.Create(
            action,
            targetType,
            targetId,
            actorId,
            actorDisplayName,
            correlationId,
            _clock.UtcNow,
            outcome,
            actingAgentId,
            previousExpiresAt,
            newExpiresAt);

        try
        {
            await _store.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Logged loudly and not rethrown. FR-041 wants an entry before success is returned,
            // but failing a completed upload because the audit account was briefly unreachable
            // would destroy the user's work to protect a record of it. The log line is the
            // fallback record, and it is deliberately at Error so it surfaces.
            _logger.LogError(
                exception,
                "Audit entry could not be written: {Action} on {TargetType} {TargetId} by {ActorId} (correlation {CorrelationId}).",
                action,
                targetType,
                targetId,
                actorId,
                correlationId);
        }
    }
}
