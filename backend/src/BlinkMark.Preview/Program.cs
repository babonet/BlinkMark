using BlinkMark.Core.Preview;
using BlinkMark.Infrastructure.Configuration;
using BlinkMark.Preview.Endpoints;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

// -----------------------------------------------------------------------------
// The preview origin (T026).
//
// This service exists on its own hostname so that a sanitizer bypass lands in an origin with no
// access to the application's session, tokens, or cookies (Principle IV).
//
// Note what is absent below: there is no authentication scheme, no authorization, no cookie
// policy, and no CORS policy. That is not an omission. The preview host's sole credential is a
// preview token, and it must never receive an Entra access token, a session cookie, or a refresh
// token — because if it could, isolating it would have achieved nothing.
// -----------------------------------------------------------------------------

builder.Services.AddBlinkMarkInfrastructure(builder.Configuration);
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseHsts();

// -----------------------------------------------------------------------------
// Response headers required by contracts/preview-origin.md.
//
// `default-src 'none'` is the one that matters most: it means the rendered document cannot reach
// the network at all. That enforces FR-016 at the browser as well as at sanitization time, so a
// tracking pixel that somehow survived the sanitizer still cannot phone home and reveal who read
// the file.
// -----------------------------------------------------------------------------

app.Use(async (context, next) =>
{
    var options = context.RequestServices.GetRequiredService<IOptions<BlinkMarkOptions>>().Value;
    var frameAncestor = string.IsNullOrWhiteSpace(options.Preview.FrameAncestor)
        ? "'none'"
        : options.Preview.FrameAncestor;

    var headers = context.Response.Headers;

    headers["Content-Security-Policy"] =
        "sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:; "
        + $"form-action 'none'; frame-ancestors {frameAncestor}";

    // Without this, the token in the URL would leak through any navigation out of the frame.
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Cache-Control"] = "private, no-store";
    headers["Cross-Origin-Resource-Policy"] = "cross-origin";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPreviewEndpoints();

await app.RunAsync();

namespace BlinkMark.Preview
{
    /// <summary>Marker type naming this assembly for <c>WebApplicationFactory</c>.</summary>
    public sealed class PreviewHostMarker;
}
