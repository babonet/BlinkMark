using BlinkMark.Api.Auth;
using BlinkMark.Api.Contracts;
using BlinkMark.Api.Middleware;
using BlinkMark.Core.Models;
using BlinkMark.Core.Retention;
using Microsoft.AspNetCore.Mvc;

namespace BlinkMark.Api.Endpoints;

/// <summary>
/// Retention changes (T070, T078).
/// </summary>
/// <remarks>
/// Owner-only, and the ownership check is a policy rather than an <c>if</c> in the handler. The
/// same policy guards deletion and download, so "who may change this file's lifetime" has one
/// answer expressed once (FR-029).
/// </remarks>
public static class RetentionEndpoints
{
    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPatch("/api/files/{fileId}/retention", UpdateAsync)
            .RequireAuthorization(AuthorizationPolicies.FileOwner)
            .WithName("UpdateRetention")
            .WithTags("Files");

        return endpoints;
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        string fileId,
        [FromBody] RetentionUpdateRequest request,
        RetentionService retention,
        AuditService audit,
        CancellationToken cancellationToken)
    {
        var result = await retention.ExtendAsync(fileId, request.ExpiresAt, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case RetentionChangeStatus.NotFound:
                return Results.NotFound();

            case RetentionChangeStatus.Rejected:
                // Audited as a denial. A refused attempt to keep a file past the ceiling is
                // exactly the kind of thing an investigation asks about later.
                await audit.RecordAsync(
                    context,
                    AuditAction.RetentionExtend,
                    AuditTargetType.File,
                    fileId,
                    AuditOutcome.Denied,
                    previousExpiresAt: result.File?.ExpiresAt,
                    newExpiresAt: request.ExpiresAt,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return Results.Problem(
                    title: "That expiry is not allowed.",
                    detail: result.Message,
                    statusCode: result.Violation == RetentionViolation.ExceedsCeiling
                        ? StatusCodes.Status422UnprocessableEntity
                        : StatusCodes.Status400BadRequest);

            default:
                var file = result.File!;

                // FR-046 requires both the old and the new expiry. One of them alone does not
                // tell you what changed.
                await audit.RecordAsync(
                    context,
                    AuditAction.RetentionExtend,
                    AuditTargetType.File,
                    fileId,
                    AuditOutcome.Success,
                    previousExpiresAt: result.PreviousExpiresAt,
                    newExpiresAt: file.ExpiresAt,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return Results.Ok(new RetentionUpdateResponse
                {
                    FileId = file.Id,
                    PreviousExpiresAt = result.PreviousExpiresAt!.Value,
                    ExpiresAt = file.ExpiresAt,
                    MaxExpiresAt = file.MaxExpiresAt,
                    RetentionNotice = RetentionPolicy.RetentionNotice(file.ExpiresAt),
                });
        }
    }
}
