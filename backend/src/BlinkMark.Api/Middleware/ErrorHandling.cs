using BlinkMark.Core.Retention;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BlinkMark.Api.Middleware;

/// <summary>
/// RFC 9457 problem details, and the rules about what an error may say (T023).
/// </summary>
/// <remarks>
/// Error responses are an information-disclosure surface, and this product has two constraints
/// most do not.
/// <para>
/// FR-001 means an unauthorized caller must learn nothing — not the filename, not the owner, not
/// whether the file exists. So an unhandled exception returns a fixed message and the correlation
/// id, and nothing else. The correlation id is the bridge: an operator can find the real error in
/// the logs, and the caller cannot.
/// </para>
/// <para>
/// FR-045 means file content, comment text, and credentials never reach a log. Exception
/// messages are the usual way that promise breaks — a serialization failure that helpfully
/// includes the document it was parsing, for instance — so the detail is dropped rather than
/// forwarded, and the structured log carries the exception separately where redaction applies.
/// </para>
/// </remarks>
public static class ErrorHandling
{
    public static IServiceCollection AddBlinkMarkProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Instance = context.HttpContext.Request.Path;
                context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.GetCorrelationId();

                // The framework adds traceId by default. It is an internal identifier and adds
                // nothing the correlation id does not already give a caller.
                context.ProblemDetails.Extensions.Remove("traceId");
            };
        });

        return services;
    }

    public static IApplicationBuilder UseBlinkMarkExceptionHandling(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(builder => builder.Run(async context =>
        {
            var feature = context.Features.Get<IExceptionHandlerFeature>();
            var exception = feature?.Error;
            var correlationId = context.GetCorrelationId();

            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("BlinkMark.Api.ErrorHandling");

            var (status, title, detail) = Map(exception);

            if (status >= StatusCodes.Status500InternalServerError)
            {
                logger.LogError(
                    exception,
                    "Unhandled exception on {Method} {Path} (correlation {CorrelationId}).",
                    context.Request.Method,
                    context.Request.Path,
                    correlationId);
            }
            else
            {
                logger.LogInformation(
                    "Request refused on {Method} {Path}: {Title} (correlation {CorrelationId}).",
                    context.Request.Method,
                    context.Request.Path,
                    title,
                    correlationId);
            }

            var problem = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Instance = context.Request.Path,
                Type = $"https://blinkmark.dev/problems/{status}",
            };

            problem.Extensions["correlationId"] = correlationId;

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
        }));

        return app;
    }

    /// <summary>
    /// Maps an exception to a status and a message a caller is allowed to see.
    /// </summary>
    /// <remarks>
    /// The retention case is the only one that returns the exception's own message, and that is
    /// safe because <see cref="RetentionViolationException"/> is constructed from policy text and
    /// timestamps. Everything else is deliberately generic — the caller gets the correlation id
    /// and the operator gets the detail.
    /// </remarks>
    private static (int Status, string Title, string? Detail) Map(Exception? exception) => exception switch
    {
        RetentionViolationException retention => (
            StatusCodes.Status422UnprocessableEntity,
            "The requested retention is not allowed.",
            retention.Message),

        UnauthorizedAccessException => (
            StatusCodes.Status401Unauthorized,
            "Authentication is required.",
            null),

        // Not found and expired are the same answer on purpose. Distinguishing them would tell
        // an unauthorized caller that a file exists (FR-032).
        FileNotFoundException or KeyNotFoundException => (
            StatusCodes.Status404NotFound,
            "Not found.",
            null),

        BadHttpRequestException => (
            StatusCodes.Status400BadRequest,
            "The request could not be understood.",
            null),

        ArgumentException => (
            StatusCodes.Status400BadRequest,
            "The request could not be understood.",
            null),

        OperationCanceledException => (
            StatusCodesExtra.ClientClosedRequest,
            "The request was cancelled.",
            null),

        _ => (
            StatusCodes.Status500InternalServerError,
            "Something went wrong.",
            null),
    };
}

/// <summary>Status codes the framework does not name.</summary>
internal static class StatusCodesExtra
{
    public const int ClientClosedRequest = 499;
}
