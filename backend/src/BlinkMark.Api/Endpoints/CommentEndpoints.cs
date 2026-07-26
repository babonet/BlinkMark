using BlinkMark.Api.Auth;
using BlinkMark.Api.Contracts;
using BlinkMark.Api.Middleware;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Anchoring;
using BlinkMark.Core.Comments;
using BlinkMark.Core.Models;
using BlinkMark.Core.Retention;

namespace BlinkMark.Api.Endpoints;

/// <summary>
/// Anchored comments (T056, T057, T059, T061, T080).
/// </summary>
/// <remarks>
/// Three habits run through every handler here and are worth stating once.
/// <para>
/// <b>Identity comes from the token.</b> The author of a comment is the authenticated principal,
/// never anything in the request body. The contract does not even have a field for it.
/// </para>
/// <para>
/// <b>The parent file is validated first, every time.</b> A comment on an expired file must read
/// as gone even if the comment's own TTL has not caught up — Cosmos has no cascading delete, so
/// the read path is what makes FR-032 true for comments.
/// </para>
/// <para>
/// <b>Bodies are literal text.</b> Nothing here renders, sanitizes, or interprets a comment body,
/// because nothing anywhere is allowed to (FR-024). An authenticated author is still an untrusted
/// source of markup.
/// </para>
/// </remarks>
public static class CommentEndpoints
{
    private const int MaxBodyLength = 10_000;

    public static IEndpointRouteBuilder MapCommentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/files/{fileId}/comments")
            .RequireAuthorization(AuthorizationPolicies.FileViewer)
            .WithTags("Comments");

        group.MapGet("/", ListAsync).WithName("ListComments");
        group.MapPost("/", CreateAsync).WithName("CreateComment");
        group.MapPatch("/{commentId}", EditAsync).WithName("EditComment");
        group.MapDelete("/{commentId}", DeleteAsync).WithName("DeleteComment");

        return endpoints;
    }

    /// <summary>Lists a file's comments, with orphan state brought up to date (T057, FR-020).</summary>
    private static async Task<IResult> ListAsync(
        string fileId,
        HttpContext context,
        IFileRepository files,
        ICommentRepository comments,
        IBlobFileStore blobs,
        OrphanDetector orphans,
        ExpiryGuard expiry,
        CancellationToken cancellationToken)
    {
        var file = expiry.Filter(await files.GetAsync(fileId, cancellationToken).ConfigureAwait(false));
        if (file is null)
        {
            return Results.NotFound();
        }

        var stored = await comments.ListByFileAsync(file.Id, cancellationToken).ConfigureAwait(false);

        // Orphan state is recomputed on read rather than trusted from storage. A comment's anchor
        // is only meaningful against a particular render, and the read path is the one place that
        // knows which render is actually being served.
        var projection = await blobs
            .ReadTextAsync(file.Id, BlobArtifact.Projection, file.RenderVersion, cancellationToken)
            .ConfigureAwait(false);

        var evaluated = projection is null
            ? stored
            : orphans.EvaluateAll(stored, projection, file.RenderVersion);

        return Results.Ok(evaluated.Select(CommentResponse.From).ToList());
    }

    /// <summary>Creates a comment or a reply (T056, FR-017, FR-018, FR-021).</summary>
    private static async Task<IResult> CreateAsync(
        string fileId,
        CreateCommentRequest request,
        HttpContext context,
        IFileRepository files,
        ICommentRepository comments,
        IBlobFileStore blobs,
        ThreadService threads,
        AnchorService anchors,
        ExpiryGuard expiry,
        INotificationQueue notifications,
        AuditService audit,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireCaller();

        var file = expiry.Filter(await files.GetAsync(fileId, cancellationToken).ConfigureAwait(false));
        if (file is null)
        {
            return Results.NotFound();
        }

        var validation = ValidateBody(request.Body);
        if (validation is not null)
        {
            return validation;
        }

        if (string.IsNullOrWhiteSpace(request.Anchor?.Exact))
        {
            return Results.Problem(
                title: "The comment is not anchored to anything.",
                detail: "A comment must quote the passage it refers to, so that it can still be shown if the passage cannot be found later.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await comments.ListByFileAsync(file.Id, cancellationToken).ConfigureAwait(false);

        var now = clock.UtcNow;
        var commentId = Identifiers.New(now);

        string threadId;
        string? parentId;
        try
        {
            (threadId, parentId) = threads.Resolve(commentId, request.ParentId, existing);
        }
        catch (KeyNotFoundException)
        {
            return Results.Problem(
                title: "The comment being replied to was not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var anchor = ToAnchor(request.Anchor, file.RenderVersion);

        // Resolved server-side at creation so a comment is never stored already orphaned without
        // anyone noticing. The client resolves against the live DOM too; this catches anchors an
        // agent supplied, which have no DOM behind them.
        var projection = await blobs
            .ReadTextAsync(file.Id, BlobArtifact.Projection, file.RenderVersion, cancellationToken)
            .ConfigureAwait(false);

        var anchorState = projection is not null && !anchors.Resolve(anchor, projection).IsResolved
            ? AnchorState.Orphaned
            : AnchorState.Anchored;

        var comment = new Comment
        {
            Id = commentId,
            FileId = file.Id,
            ThreadId = threadId,
            ParentId = parentId,
            Body = request.Body.Trim(),
            AuthorId = caller.UserId,
            AuthorDisplayName = caller.DisplayName,
            ActingAgentId = caller.ActingAgentId,
            CreatedAt = now,
            Anchor = anchor,
            AnchorState = anchorState,
            // Derived from the parent file, so a comment can never outlive the thing it is about
            // (research.md R7).
            Ttl = RetentionPolicy.TtlSeconds(file.ExpiresAt, now),
        };

        var created = await comments.CreateAsync(comment, cancellationToken).ConfigureAwait(false);

        await audit.RecordAsync(
            context,
            AuditAction.CommentCreate,
            AuditTargetType.Comment,
            created.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Enqueue-and-forget. The queue adapter swallows its own failures, so a notification
        // problem cannot fail a comment that has already been written (FR-037).
        await notifications.EnqueueAsync(
            new NotificationMessage
            {
                FileId = file.Id,
                CommentId = created.Id,
                ThreadId = created.ThreadId,
                ActorId = caller.UserId,
                ActorDisplayName = caller.DisplayName,
                ActingAgentId = caller.ActingAgentId,
                OccurredAt = now,
                CorrelationId = context.GetCorrelationId(),
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Created(
            $"/api/files/{file.Id}/comments/{created.Id}",
            CommentResponse.From(created));
    }

    /// <summary>Edits the caller's own comment (T059, FR-025).</summary>
    private static async Task<IResult> EditAsync(
        string fileId,
        string commentId,
        EditCommentRequest request,
        HttpContext context,
        IFileRepository files,
        ICommentRepository comments,
        ExpiryGuard expiry,
        AuditService audit,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireCaller();

        var file = expiry.Filter(await files.GetAsync(fileId, cancellationToken).ConfigureAwait(false));
        if (file is null)
        {
            return Results.NotFound();
        }

        var existing = await comments.GetAsync(file.Id, commentId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.IsDeleted)
        {
            return Results.NotFound();
        }

        if (!string.Equals(existing.AuthorId, caller.UserId, StringComparison.Ordinal))
        {
            await audit.RecordAsync(
                context,
                AuditAction.CommentEdit,
                AuditTargetType.Comment,
                commentId,
                AuditOutcome.Denied,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Only the author may edit. Not the file owner, and not an administrator — there is
            // no administrator, and putting words in someone's mouth is not a capability worth
            // building (FR-025).
            return Results.Forbid();
        }

        var validation = ValidateBody(request.Body);
        if (validation is not null)
        {
            return validation;
        }

        // The anchor is untouched. An edit changes what someone said, not what they said it about.
        var updated = existing with
        {
            Body = request.Body.Trim(),
            EditedAt = clock.UtcNow,
        };

        var saved = await comments.ReplaceAsync(updated, cancellationToken).ConfigureAwait(false);

        await audit.RecordAsync(
            context,
            AuditAction.CommentEdit,
            AuditTargetType.Comment,
            saved.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Results.Ok(CommentResponse.From(saved));
    }

    /// <summary>Soft-deletes the caller's own comment (T059, FR-025).</summary>
    private static async Task<IResult> DeleteAsync(
        string fileId,
        string commentId,
        HttpContext context,
        IFileRepository files,
        ICommentRepository comments,
        ExpiryGuard expiry,
        AuditService audit,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireCaller();

        var file = expiry.Filter(await files.GetAsync(fileId, cancellationToken).ConfigureAwait(false));
        if (file is null)
        {
            return Results.NotFound();
        }

        var existing = await comments.GetAsync(file.Id, commentId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.IsDeleted)
        {
            return Results.NotFound();
        }

        if (!string.Equals(existing.AuthorId, caller.UserId, StringComparison.Ordinal))
        {
            await audit.RecordAsync(
                context,
                AuditAction.CommentDelete,
                AuditTargetType.Comment,
                commentId,
                AuditOutcome.Denied,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return Results.Forbid();
        }

        // Soft delete, so replies to this comment survive. Hard-deleting a comment mid-thread
        // would silently destroy other people's work.
        await comments.SoftDeleteAsync(file.Id, commentId, cancellationToken).ConfigureAwait(false);

        await audit.RecordAsync(
            context,
            AuditAction.CommentDelete,
            AuditTargetType.Comment,
            commentId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static IResult? ValidateBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Results.Problem(
                title: "The comment is empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (body.Length > MaxBodyLength)
        {
            return Results.Problem(
                title: "The comment is too long.",
                detail: $"Comments are limited to {MaxBodyLength:N0} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }

    private static Anchor ToAnchor(AnchorRequest request, string renderVersion) => new()
    {
        Kind = string.Equals(request.Kind, "region", StringComparison.OrdinalIgnoreCase)
            ? AnchorKind.Region
            : AnchorKind.Text,
        Exact = request.Exact,
        Prefix = request.Prefix ?? string.Empty,
        Suffix = request.Suffix ?? string.Empty,
        Start = request.Start,
        End = request.End,
        ContainerPath = request.ContainerPath,
        FractionalRect = request.FractionalRect is null
            ? null
            : new FractionalRect
            {
                X = request.FractionalRect.X,
                Y = request.FractionalRect.Y,
                Width = request.FractionalRect.Width,
                Height = request.FractionalRect.Height,
            },
        // Stamped server-side from the file, never taken from the client. An anchor that claimed
        // a different render version would resolve against the wrong text.
        RenderVersion = renderVersion,
    };
}
