using BlinkMark.Api.Auth;
using BlinkMark.Api.Endpoints;
using BlinkMark.Api.Middleware;
using BlinkMark.Infrastructure.Configuration;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi.Models;

namespace BlinkMark.Api;

/// <summary>
/// The API's service registration and request pipeline, in one reusable place.
/// </summary>
/// <remarks>
/// Extracted from <c>Program.cs</c> so that the local development host can run the *same* wiring
/// rather than a second copy of it. That matters more than it might appear: a local host that
/// assembles its own middleware chain will eventually order it differently, and the first thing a
/// developer notices is that something works locally and fails deployed — or worse, that a
/// security header or an authorization call is present in one and absent in the other.
/// <para>
/// Nothing here knows about local development. The local host reuses these methods and then
/// replaces the adapters behind them; it does not get a different pipeline, different headers, or
/// a different authorization model.
/// </para>
/// </remarks>
public static class ApiHost
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddBlinkMarkInfrastructure(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<AuditService>();

        ConfigureRequestLimits(builder);

        builder.Services.AddBlinkMarkAuthentication(builder.Configuration);
        builder.Services.AddBlinkMarkAuthorization();
        builder.Services.AddBlinkMarkProblemDetails();

        builder.Services.AddApplicationInsightsTelemetry();

        // The preview token travels in a query string (contracts/preview-origin.md). Excluding
        // query strings from request telemetry is one of the mitigations that makes that
        // acceptable.
        builder.Services.AddSingleton<Microsoft.ApplicationInsights.Extensibility.ITelemetryInitializer,
            Observability.QueryStringRedactingInitializer>();

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "BlinkMark API",
                Version = "v1",
                Description =
                    "Tenant-internal sharing of short-lived HTML and Markdown drafts with anchored review comments. "
                    + "Every operation requires an authenticated member of the owning organization.",
            });

            options.AddSecurityDefinition("entra", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "Microsoft Entra ID, single tenant.",
            });
        });

        builder.Services.AddCors(options =>
        {
            var allowedOrigin = builder.Configuration["BlinkMark:Cors:AllowedOrigin"];
            options.AddDefaultPolicy(policy =>
            {
                if (!string.IsNullOrWhiteSpace(allowedOrigin))
                {
                    policy.WithOrigins(allowedOrigin)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials()
                        .WithExposedHeaders(CorrelationIdMiddleware.HeaderName);
                }
            });
        });

        // Container Apps terminates TLS at the ingress, so the scheme has to come from the
        // forwarded headers or every generated URL would be http.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }

    /// <summary>
    /// Ties the transport's request limits to the configured upload limit.
    /// </summary>
    /// <remarks>
    /// Kestrel refuses a body over 30 MB by default and the form reader refuses a multipart body
    /// over 128 MB. Both sat silently above the 10 MB upload limit, so nothing was wrong — and
    /// nothing said so either. Raising the upload limit to 20 MB left one of those defaults only
    /// 43% clear of it, and the next raise would cross it.
    /// <para>
    /// Crossing it fails badly rather than obviously: Kestrel aborts the request before any
    /// application code runs, so the uploader gets a bare 413 instead of
    /// <c>UploadValidator</c>'s message naming the limit and the actual size. Deriving the
    /// transport limit from the configured one keeps them in step and keeps the explanation
    /// coming from the place that knows the reason.
    /// </para>
    /// <para>
    /// The headroom covers multipart framing — boundaries, part headers, the filename — which is
    /// a few hundred bytes in practice. It is deliberately not generous: this is the last line
    /// before an unbounded request body, and <c>UploadValidator</c> is what should be doing the
    /// refusing.
    /// </para>
    /// </remarks>
    private static void ConfigureRequestLimits(WebApplicationBuilder builder)
    {
        const long MultipartFramingHeadroom = 64 * 1024;

        var maxUploadBytes =
            builder.Configuration.GetValue<long?>($"{BlinkMarkOptions.SectionName}:Upload:MaxSizeBytes")
            ?? new UploadOptions().MaxSizeBytes;

        var maxRequestBytes = maxUploadBytes + MultipartFramingHeadroom;

        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = maxRequestBytes;
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = maxRequestBytes;
        });
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseBlinkMarkExceptionHandling();

        app.UseHsts();

        app.Use(async (context, next) =>
        {
            // The API returns JSON only. These headers cost nothing and close off the class of
            // attacks that depend on a browser deciding to treat a response as something it is not.
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-site";
            await next();
        });

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        else
        {
            // The document itself is public — it describes the shape of the API, not its contents
            // — but the interactive UI is not shipped to production.
            app.UseSwagger();
        }

        MapHealth(app);

        app.MapFileEndpoints();
        app.MapCommentEndpoints();
        app.MapRetentionEndpoints();
    }

    /// <summary>
    /// Liveness and readiness.
    /// </summary>
    /// <remarks>
    /// Unauthenticated by necessity: the Container Apps probe and the availability test cannot
    /// hold a credential. Liveness returns nothing but liveness, so there is nothing to protect.
    /// </remarks>
    private static void MapHealth(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapGet("/health/ready", async (IServiceProvider services, CancellationToken cancellationToken) =>
            {
                var cosmos = services.GetService<Microsoft.Azure.Cosmos.CosmosClient>();

                if (cosmos is null)
                {
                    // No Cosmos client registered at all. True of the local development host,
                    // where storage is in-memory and there is no external dependency to probe.
                    return Results.Ok(new { status = "ready" });
                }

                try
                {
                    await cosmos.ReadAccountAsync();
                    return Results.Ok(new { status = "ready" });
                }
                catch (Exception)
                {
                    // No detail. A readiness probe that names the failing dependency is a
                    // reconnaissance endpoint for anyone who can reach it.
                    return Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
    }
}
