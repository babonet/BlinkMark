using System.Diagnostics;

namespace BlinkMark.Api.Middleware;

/// <summary>
/// Establishes a correlation id for every request (T022).
/// </summary>
/// <remarks>
/// Principle V requires the correlation id to survive frontend to API to storage. That only
/// works if it is established once, at the edge, and then read from a single place — so this
/// middleware runs before anything else and the id is available through
/// <see cref="CorrelationContext"/> everywhere downstream, including in the preview token, where
/// it lets a content fetch on a different host be tied back to the metadata read that authorized
/// it.
/// <para>
/// A client-supplied id is accepted but sanitized. Correlation ids end up in log lines and in
/// storage requests; an unbounded attacker-controlled string in either is a log-injection
/// problem, not a tracing feature.
/// </para>
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Items[CorrelationContext.ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        // Flows into Application Insights automatically through the activity, which is what
        // makes a trace and an audit entry line up without a second lookup.
        Activity.Current?.SetBaggage("blinkmark.correlationId", correlationId);

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return Core.Models.Identifiers.New();
        }

        var candidate = values.ToString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxLength)
        {
            return Core.Models.Identifiers.New();
        }

        // Alphanumerics, hyphen, and underscore only. Anything else could break a log line into
        // two, which is how a forged audit record gets written without ever touching the store.
        foreach (var character in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return Core.Models.Identifiers.New();
            }
        }

        return candidate;
    }
}

/// <summary>Reads the correlation id established for the current request.</summary>
public static class CorrelationContext
{
    internal const string ItemKey = "BlinkMark.CorrelationId";

    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string correlationId
            ? correlationId
            : "unknown";
}
