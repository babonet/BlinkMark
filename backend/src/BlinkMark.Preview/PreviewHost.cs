using BlinkMark.Infrastructure.Configuration;
using BlinkMark.Preview.Endpoints;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace BlinkMark.Preview;

/// <summary>
/// The preview origin's service registration and request pipeline, in one reusable place.
/// </summary>
/// <remarks>
/// Extracted for the same reason as <c>BlinkMark.Api.ApiHost</c>: the local development host runs
/// this exact code rather than a second copy. Here the stakes are higher than convenience — the
/// Content-Security-Policy below is the browser-side half of Principle IV, and a local host that
/// assembled its own headers could quietly omit it. You would then develop against a preview
/// origin that permits network access, and never find out until production.
/// </remarks>
public static class PreviewHost
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        // -----------------------------------------------------------------------------
        // Note what is absent: there is no authentication scheme, no authorization, no cookie
        // policy, and no CORS policy. That is not an omission. The preview host's sole credential
        // is a preview token, and it must never receive an Entra access token, a session cookie,
        // or a refresh token — because if it could, isolating it would have achieved nothing.
        // -----------------------------------------------------------------------------
        builder.Services.AddBlinkMarkInfrastructure(builder.Configuration);
        builder.Services.AddApplicationInsightsTelemetry();

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseHsts();

        // -----------------------------------------------------------------------------
        // Response headers required by contracts/preview-origin.md.
        //
        // `default-src 'none'` is the one that matters most: it means the rendered document
        // cannot reach the network at all. That enforces FR-016 at the browser as well as at
        // sanitization time, so a tracking pixel that somehow survived the sanitizer still cannot
        // phone home and reveal who read the file.
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
    }
}
