using Azure;
using Azure.Data.Tables;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;
using BlinkMark.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlinkMark.Infrastructure.Audit;

/// <summary>
/// The audit trail in Table Storage (T018).
/// </summary>
/// <remarks>
/// This class has one public operation and that is the whole design. FR-044 asks for no
/// interface that permits application-level modification or deletion, and the most reliable way
/// to provide none is to write none. There is no <c>UpdateAsync</c>, no <c>DeleteAsync</c>, and
/// no <c>UpsertAsync</c> here — not because nobody would call them, but so that nobody can. A
/// test in the integration suite asserts that no such member is ever added.
/// <para>
/// Being honest about the limit: Azure Storage immutability policies apply to blob containers,
/// not to tables, and there is no add-only RBAC role for the table data plane. Append-only here
/// is enforced by four layers — this interface, the resource lock on the account, the separate
/// storage account, and that test — but it is application-enforced, not platform-enforced. If
/// platform enforcement is later required, the escalation path is a dual write to an append blob
/// under a time-based immutability policy, keeping the table as the queryable projection
/// (research.md R8).
/// </para>
/// </remarks>
public sealed class TableAuditStore : IAuditStore
{
    private readonly TableClient _table;
    private readonly ILogger<TableAuditStore> _logger;

    public TableAuditStore(
        TableServiceClient tableService,
        IOptions<BlinkMarkOptions> options,
        ILogger<TableAuditStore> logger)
    {
        _table = tableService.GetTableClient(options.Value.Audit.TableName);
        _logger = logger;
    }

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var tableEntity = new TableEntity(entry.PartitionKey, entry.RowKey)
        {
            ["ActorId"] = entry.ActorId,
            ["ActorDisplayName"] = entry.ActorDisplayName,
            ["ActingAgentId"] = entry.ActingAgentId,
            ["Action"] = entry.Action.ToString(),
            ["TargetType"] = entry.TargetType.ToString(),
            ["TargetId"] = entry.TargetId,
            ["Outcome"] = entry.Outcome.ToString(),
            ["OccurredAt"] = entry.OccurredAt.UtcDateTime,
            ["CorrelationId"] = entry.CorrelationId,
            ["PreviousExpiresAt"] = entry.PreviousExpiresAt?.UtcDateTime,
            ["NewExpiresAt"] = entry.NewExpiresAt?.UtcDateTime,
        };

        try
        {
            // AddEntityAsync, never UpsertEntityAsync. An upsert against an existing row key
            // would overwrite history, which is the one thing this store must not be able to do.
            await _table.AddEntityAsync(tableEntity, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            // A duplicate row key means the same entry was written twice — a retry, almost
            // certainly. The existing row is authoritative and is left exactly as it is.
            _logger.LogDebug(
                "Audit entry {PartitionKey}/{RowKey} already exists; leaving the original untouched.",
                entry.PartitionKey,
                entry.RowKey);
        }
    }
}
