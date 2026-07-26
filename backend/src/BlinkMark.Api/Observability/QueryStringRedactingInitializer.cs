using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace BlinkMark.Api.Observability;

/// <summary>
/// Strips query strings from request telemetry.
/// </summary>
/// <remarks>
/// The preview token travels in the URL, because a sandboxed frame without
/// <c>allow-same-origin</c> cannot carry a cookie or an <c>Authorization</c> header
/// (contracts/preview-origin.md). Excluding query strings from telemetry is one of the
/// mitigations that makes a credential in a URL acceptable — without it, every issued token
/// would be sitting in Application Insights for the retention period, long outliving the
/// fifteen minutes it was meant to exist for.
/// </remarks>
public sealed class QueryStringRedactingInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        switch (telemetry)
        {
            case RequestTelemetry request:
                request.Url = StripQuery(request.Url);
                break;

            case DependencyTelemetry dependency:
                dependency.Data = StripQuery(dependency.Data);
                break;
        }
    }

    private static Uri? StripQuery(Uri? uri)
    {
        if (uri is null || string.IsNullOrEmpty(uri.Query))
        {
            return uri;
        }

        return new UriBuilder(uri) { Query = string.Empty }.Uri;
    }

    private static string? StripQuery(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        var index = url.IndexOf('?', StringComparison.Ordinal);
        return index < 0 ? url : url[..index];
    }
}
