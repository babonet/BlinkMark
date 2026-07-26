using System.Text;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;
using BlinkMark.Core.Preview;

namespace BlinkMark.Preview.Endpoints;

/// <summary>
/// The one endpoint that returns rendered file content to a browser (T039, T120).
/// </summary>
/// <remarks>
/// It serves a stored render artifact and nothing else. Sanitization happened once at upload;
/// this host never renders, never reads Cosmos, and never issues a token.
/// <para>
/// Authorization is the preview token and only the preview token. The 15-minute ceiling limits
/// how long a leaked URL stays useful — it does not limit a reviewer, because the token gates the
/// document fetch rather than the review session. Once the frame has rendered, nothing
/// re-validates it, and commenting runs against the API with the user's ordinary session.
/// </para>
/// <para>
/// This is also the only place the <c>preview</c> audit entry can be written accurately. The API
/// mints tokens but cannot know whether one was ever redeemed, which is why FR-041 lists
/// <c>view</c> and <c>preview</c> as separate actions in the first place.
/// </para>
/// </remarks>
public static class PreviewEndpoints
{
    public static IEndpointRouteBuilder MapPreviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/p/{fileId}", ServeAsync);
        return endpoints;
    }

    private static async Task<IResult> ServeAsync(
        string fileId,
        HttpContext context,
        PreviewTokenService tokens,
        IBlobFileStore blobs,
        IAuditStore audit,
        IClock clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("BlinkMark.Preview");

        // The token is read from the query string because a sandboxed frame without
        // allow-same-origin cannot attach a header or a cookie to its own document request.
        var token = context.Request.Query["t"].ToString();

        var validation = await tokens.ValidateAsync(token, fileId, cancellationToken).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            // A 401 says nothing at all — no content, no filename, no confirmation that the file
            // exists (FR-001). The SPA treats it as "re-mint and retry", never as an error the
            // reader should see (contracts/preview-origin.md).
            logger.LogInformation(
                "Preview refused for file {FileId}: {Failure}.",
                fileId,
                validation.Failure);

            return Results.StatusCode(validation.StatusCode);
        }

        var claims = validation.Claims!;

        var render = await blobs
            .ReadTextAsync(fileId, BlobArtifact.Render, claims.RenderVersion, cancellationToken)
            .ConfigureAwait(false);

        if (render is null)
        {
            // Expired and never-existed are the same answer, deliberately. Distinguishing them
            // would confirm that a file used to be here (FR-032).
            await WriteAuditAsync(audit, claims, clock, AuditOutcome.Denied, cancellationToken)
                .ConfigureAwait(false);

            return Results.NotFound();
        }

        // Written before the response, using the token's own subject, acting agent, and
        // correlation id. Because tokens are reusable and re-minted on reload, one reading
        // session may produce several entries — which is correct, since each records an actual
        // content fetch.
        await WriteAuditAsync(audit, claims, clock, AuditOutcome.Success, cancellationToken)
            .ConfigureAwait(false);

        return Results.Text(render, "text/html", Encoding.UTF8);
    }

    private static Task WriteAuditAsync(
        IAuditStore audit,
        PreviewTokenClaims claims,
        IClock clock,
        AuditOutcome outcome,
        CancellationToken cancellationToken)
    {
        var entry = AuditEntry.Create(
            AuditAction.Preview,
            AuditTargetType.File,
            claims.FileId,
            claims.Subject,
            // The preview host never sees a directory, and must not call one — it has no
            // credential that would let it. The API already recorded the display name on the
            // corresponding `view` entry, which shares this correlation id.
            actorDisplayName: claims.Subject,
            claims.CorrelationId,
            clock.UtcNow,
            outcome,
            claims.ActingAgentId);

        return audit.AppendAsync(entry, cancellationToken);
    }
}
