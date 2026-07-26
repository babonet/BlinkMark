using System.IO.Compression;
using System.Text;
using BlinkMark.Api.Auth;
using BlinkMark.Api.Contracts;
using BlinkMark.Api.Middleware;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Anchoring;
using BlinkMark.Core.Export;
using BlinkMark.Core.Models;
using BlinkMark.Core.Preview;
using BlinkMark.Core.Quotas;
using BlinkMark.Core.Rendering;
using BlinkMark.Core.Retention;
using BlinkMark.Core.Upload;
using BlinkMark.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BlinkMark.Api.Endpoints;

/// <summary>
/// File upload, retrieval, listing, and deletion (T035, T040, T041, T045, T073).
/// </summary>
public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/files")
            .RequireAuthorization(AuthorizationPolicies.TenantMember)
            .WithTags("Files");

        group.MapPost("/", UploadAsync)
            .WithName("UploadFile")
            .DisableAntiforgery();

        group.MapGet("/", ListAsync).WithName("ListFiles");
        group.MapGet("/{fileId}", GetAsync).WithName("GetFile");
        group.MapGet("/{fileId}/content", GetContentAsync).WithName("GetFileContent");

        group.MapDelete("/{fileId}", DeleteAsync)
            .WithName("DeleteFile")
            .RequireAuthorization(AuthorizationPolicies.FileOwner);

        group.MapGet("/{fileId}/download", DownloadAsync)
            .WithName("DownloadFile")
            .RequireAuthorization(AuthorizationPolicies.FileOwner);

        return endpoints;
    }

    /// <summary>
    /// Accepts an upload (T035, FR-006 to FR-010).
    /// </summary>
    /// <remarks>
    /// Order matters here. The quota is checked before anything is written, so a refused upload
    /// leaves nothing behind. The render happens before the document is created, so a file can
    /// never exist without something to preview. And <c>maxExpiresAt</c> is computed once, from
    /// the upload time, and stored — from this point on it is data that every later write is
    /// checked against (FR-028).
    /// </remarks>
    private static async Task<IResult> UploadAsync(
        HttpContext context,
        IFormFile file,
        IFileRepository files,
        IBlobFileStore blobs,
        UploadValidator validator,
        RenderPipeline renderer,
        QuotaService quotas,
        PreviewTokenService previewTokens,
        AuditService audit,
        IClock clock,
        IOptions<BlinkMarkOptions> options,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireCaller();

        var quota = await quotas.CheckUploadAllowedAsync(caller.UserId, cancellationToken).ConfigureAwait(false);
        if (!quota.IsAllowed)
        {
            await audit.RecordAsync(
                context,
                AuditAction.Upload,
                AuditTargetType.File,
                "n/a",
                AuditOutcome.Denied,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return Results.Problem(
                title: "Upload limit reached.",
                detail: quota.Message,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        await using var stream = file.OpenReadStream();
        var validation = validator.Validate(file.FileName, stream);

        if (!validation.IsValid)
        {
            await audit.RecordAsync(
                context,
                AuditAction.Upload,
                AuditTargetType.File,
                "n/a",
                AuditOutcome.Denied,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return Results.Problem(
                title: "The file could not be accepted.",
                detail: validation.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var now = clock.UtcNow;

        // System-generated, never derived from the uploaded filename (FR-008).
        var fileId = Identifiers.New(now);
        var render = renderer.Render(validation.Content, validation.ContentType);

        var expiresAt = RetentionPolicy.DefaultExpiresAt(now);
        var maxExpiresAt = RetentionPolicy.MaxExpiresAt(now);

        await WriteArtifactsAsync(blobs, fileId, validation, render, expiresAt, cancellationToken)
            .ConfigureAwait(false);

        var record = new FileRecord
        {
            Id = fileId,
            // Kept for display only. It is attacker-controlled text that happens to be useful to
            // a human, and it never becomes a path, a header, or a link.
            DisplayName = Path.GetFileName(file.FileName),
            ContentType = validation.ContentType,
            SizeBytes = file.Length,
            OwnerId = caller.UserId,
            OwnerDisplayName = caller.DisplayName,
            UploadedAt = now,
            ExpiresAt = expiresAt,
            MaxExpiresAt = maxExpiresAt,
            RenderVersion = render.RenderVersion,
            BlobPath = $"originals/{fileId}",
            Ttl = RetentionPolicy.TtlSeconds(expiresAt, now),
        };

        var created = await files.CreateAsync(record, cancellationToken).ConfigureAwait(false);

        await audit.RecordAsync(
            context,
            AuditAction.Upload,
            AuditTargetType.File,
            created.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var response = await BuildDetailAsync(
            created, caller, previewTokens, options.Value, context, cancellationToken).ConfigureAwait(false);

        return Results.Created($"/api/files/{created.Id}", response);
    }

    /// <summary>Returns a file's metadata plus a fresh preview URL (T040).</summary>
    private static async Task<IResult> GetAsync(
        string fileId,
        HttpContext context,
        IFileRepository files,
        ExpiryGuard expiry,
        PreviewTokenService previewTokens,
        AuditService audit,
        IOptions<BlinkMarkOptions> options,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireCaller();

        var file = expiry.Filter(await files.GetAsync(fileId, cancellationToken).ConfigureAwait(false));
        if (file is null)
        {
            // Missing and expired are the same answer (FR-032).
            return Results.NotFound();
        }

        await audit.RecordAsync(
            context,
            AuditAction.View,
            AuditTargetType.File,
            file.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var response = await BuildDetailAsync(
            file, caller, previewTokens, options.Value, context, cancellationToken).ConfigureAwait(false);

        return Results.Ok(response);
    }

    /// <summary>Lists the caller's live files with their quota position (T041).</summary>
    private static async Task<IResult> ListAsync(
        HttpContext context,
        IFileRepository files,
        ExpiryGuard expiry,
        QuotaService quotas,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireCaller();

        var owned = await files.ListByOwnerAsync(caller.UserId, cancellationToken).ConfigureAwait(false);
        var live = expiry.Filter(owned);
        var quota = await quotas.GetStatusAsync(caller.UserId, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new FileListResponse
        {
            Files = live.Select(FileSummaryResponse.From).ToList(),
            Quota = new QuotaStatusResponse
            {
                LiveFiles = quota.LiveFiles,
                MaxLiveFiles = quota.MaxLiveFiles,
            },
        });
    }

    /// <summary>Returns the text projection an agent reads (T094, FR-051).</summary>
    private static async Task<IResult> GetContentAsync(
        string fileId,
        HttpContext context,
        IFileRepository files,
        IBlobFileStore blobs,
        ExpiryGuard expiry,
        AuditService audit,
        CancellationToken cancellationToken)
    {
        var file = expiry.Filter(await files.GetAsync(fileId, cancellationToken).ConfigureAwait(false));
        if (file is null)
        {
            return Results.NotFound();
        }

        var text = await blobs
            .ReadTextAsync(file.Id, BlobArtifact.Projection, file.RenderVersion, cancellationToken)
            .ConfigureAwait(false);

        if (text is null)
        {
            return Results.NotFound();
        }

        // The structured view is derived from the stored render rather than stored separately, so
        // there is no second artifact to keep in step and no migration for files uploaded before
        // it existed. Both projections come from the same sanitized HTML, which is what keeps
        // their offsets in agreement (DocumentProjectionTests).
        var html = await blobs
            .ReadTextAsync(file.Id, BlobArtifact.Render, file.RenderVersion, cancellationToken)
            .ConfigureAwait(false);

        var blocks = html is null
            ? []
            : DocumentProjection.Project(html).Select(DocumentBlockResponse.From).ToList();

        await audit.RecordAsync(
            context,
            AuditAction.View,
            AuditTargetType.File,
            file.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Results.Ok(new FileContentResponse
        {
            FileId = file.Id,
            DisplayName = file.DisplayName,
            ContentType = file.ContentType.ToString().ToLowerInvariant(),
            RenderVersion = file.RenderVersion,
            Text = text,
            Blocks = blocks,
            ExpiresAt = file.ExpiresAt,
        });
    }

    /// <summary>
    /// Downloads the file with every comment on it. Owner only (T075, FR-031).
    /// </summary>
    /// <remarks>
    /// A zip of two entries: the original content exactly as uploaded, and a JSON sidecar holding
    /// every comment. The original rather than the render, because the owner wants their file
    /// back, not BlinkMark's sanitized view of it — and because the render already exists only to
    /// be displayed safely in a browser, which a downloaded copy is not.
    /// <para>
    /// Restricted to the owner by the same policy that guards deletion. A viewer can read the
    /// file and its comments through the API; what they cannot do is take a permanent copy that
    /// outlives the retention window, which is the boundary FR-031 draws.
    /// </para>
    /// </remarks>
    private static async Task<IResult> DownloadAsync(
        string fileId,
        HttpContext context,
        IFileRepository files,
        ICommentRepository comments,
        IBlobFileStore blobs,
        DownloadBundleBuilder bundler,
        OrphanDetector orphans,
        ExpiryGuard expiry,
        AuditService audit,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var file = expiry.Filter(await files.GetAsync(fileId, cancellationToken).ConfigureAwait(false));
        if (file is null)
        {
            return Results.NotFound();
        }

        var original = await blobs
            .ReadTextAsync(file.Id, BlobArtifact.Original, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (original is null)
        {
            return Results.NotFound();
        }

        // Every comment, including orphaned and deleted ones. The builder decides what each one
        // exports as; the endpoint's job is simply not to filter any of them out here.
        var stored = await comments.ListByFileAsync(file.Id, cancellationToken).ConfigureAwait(false);

        // Orphan state is recomputed against the current projection, exactly as the read path
        // does. Exporting the state recorded at write time would produce a bundle that disagrees
        // with what the reviewer was looking at when they pressed download.
        var projection = await blobs
            .ReadTextAsync(file.Id, BlobArtifact.Projection, file.RenderVersion, cancellationToken)
            .ConfigureAwait(false);

        var all = projection is null
            ? stored
            : orphans.EvaluateAll(stored, projection, file.RenderVersion);

        var bundle = bundler.Build(file, all, clock.UtcNow);

        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var contentEntry = archive.CreateEntry(SafeEntryName(file), CompressionLevel.Optimal);
            await using (var entryStream = contentEntry.Open())
            await using (var writer = new StreamWriter(entryStream, Encoding.UTF8))
            {
                await writer.WriteAsync(original).ConfigureAwait(false);
            }

            var commentsEntry = archive.CreateEntry("comments.json", CompressionLevel.Optimal);
            await using (var entryStream = commentsEntry.Open())
            {
                var json = bundler.Serialize(bundle);
                await entryStream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            }
        }

        await audit.RecordAsync(
            context,
            AuditAction.Download,
            AuditTargetType.File,
            file.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Results.File(
            buffer.ToArray(),
            "application/zip",
            $"{Path.GetFileNameWithoutExtension(SafeEntryName(file))}-blinkmark.zip");
    }

    /// <summary>
    /// A zip entry name derived from the display name, with the path stripped.
    /// </summary>
    /// <remarks>
    /// The display name came from a user and has never been trusted as a path. Reducing it to a
    /// bare filename here keeps a crafted name like <c>../../evil.html</c> from becoming a zip
    /// traversal entry in whatever tool the owner opens the archive with.
    /// </remarks>
    private static string SafeEntryName(FileRecord file)
    {
        var candidate = Path.GetFileName(file.DisplayName.Replace('\\', '/'));

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(invalid, '_');
        }

        candidate = candidate.Trim('.', ' ');

        return string.IsNullOrEmpty(candidate)
            ? $"{file.Id}.{(file.ContentType == FileContentType.Html ? "html" : "md")}"
            : candidate;
    }

    /// <summary>Deletes a file early. Owner only (T073, FR-029).</summary>
    private static async Task<IResult> DeleteAsync(
        string fileId,
        HttpContext context,
        IFileRepository files,
        ICommentRepository comments,
        IBlobFileStore blobs,
        ExpiryGuard expiry,
        AuditService audit,
        CancellationToken cancellationToken)
    {
        var file = expiry.Filter(await files.GetAsync(fileId, cancellationToken).ConfigureAwait(false));
        if (file is null)
        {
            return Results.NotFound();
        }

        // Comments, then blobs, then the document. Deleting a file removes all three — and never
        // its audit entries (FR-031, FR-043).
        await comments.DeleteByFileAsync(file.Id, cancellationToken).ConfigureAwait(false);
        await blobs.DeleteAllAsync(file.Id, file.RenderVersion, cancellationToken).ConfigureAwait(false);
        await files.DeleteAsync(file.Id, cancellationToken).ConfigureAwait(false);

        await audit.RecordAsync(
            context,
            AuditAction.FileDelete,
            AuditTargetType.File,
            file.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task WriteArtifactsAsync(
        IBlobFileStore blobs,
        string fileId,
        UploadValidationResult validation,
        RenderResult render,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var original = new MemoryStream(Encoding.UTF8.GetBytes(validation.Content));
        await blobs.WriteAsync(
            fileId,
            BlobArtifact.Original,
            original,
            validation.ContentType == FileContentType.Html ? "text/html" : "text/markdown",
            expiresAt,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await using var rendered = new MemoryStream(Encoding.UTF8.GetBytes(render.Html));
        await blobs.WriteAsync(
            fileId,
            BlobArtifact.Render,
            rendered,
            "text/html",
            expiresAt,
            render.RenderVersion,
            cancellationToken).ConfigureAwait(false);

        await using var projection = new MemoryStream(Encoding.UTF8.GetBytes(render.TextProjection));
        await blobs.WriteAsync(
            fileId,
            BlobArtifact.Projection,
            projection,
            "text/plain",
            expiresAt,
            render.RenderVersion,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<FileDetailResponse> BuildDetailAsync(
        FileRecord file,
        CallerIdentity caller,
        PreviewTokenService previewTokens,
        BlinkMarkOptions options,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var token = await previewTokens.IssueAsync(
            file.Id,
            file.RenderVersion,
            caller.UserId,
            context.GetCorrelationId(),
            caller.ActingAgentId,
            cancellationToken).ConfigureAwait(false);

        var previewOrigin = options.Preview.Origin.TrimEnd('/');

        return new FileDetailResponse
        {
            Id = file.Id,
            DisplayName = file.DisplayName,
            ContentType = file.ContentType.ToString().ToLowerInvariant(),
            SizeBytes = file.SizeBytes,
            OwnerId = file.OwnerId,
            OwnerDisplayName = file.OwnerDisplayName,
            UploadedAt = file.UploadedAt,
            ExpiresAt = file.ExpiresAt,
            MaxExpiresAt = file.MaxExpiresAt,
            PreviewUrl = $"{previewOrigin}/p/{file.Id}?t={Uri.EscapeDataString(token)}",
            AccessScopeNotice =
                "Anyone in your organization who has this link can open and comment on this file. "
                + "There is no per-person access list — the link is the access grant.",
            RetentionNotice = RetentionPolicy.RetentionNotice(file.ExpiresAt),
            IsOwner = string.Equals(file.OwnerId, caller.UserId, StringComparison.Ordinal),
        };
    }
}
