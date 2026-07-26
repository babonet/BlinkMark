using BlinkMark.Api.Auth;
using BlinkMark.Api.Endpoints;
using BlinkMark.Api.Middleware;
using BlinkMark.Infrastructure.Configuration;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------------
// Configuration (T024)
//
// Environment variables only, supplied by container-apps.bicep. Every value is an endpoint or
// an identifier — there is no connection string, no account key, and no client secret, because
// every resource has local authentication disabled and would refuse one (Principle VII).
// -----------------------------------------------------------------------------

builder.Configuration.AddEnvironmentVariables();

builder.Services.AddBlinkMarkInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<AuditService>();

builder.Services.AddBlinkMarkAuthentication(builder.Configuration);
builder.Services.AddBlinkMarkAuthorization();
builder.Services.AddBlinkMarkProblemDetails();

builder.Services.AddApplicationInsightsTelemetry();

// The preview token travels in a query string (contracts/preview-origin.md). Excluding query
// strings from request telemetry is one of the mitigations that makes that acceptable.
builder.Services.AddSingleton<Microsoft.ApplicationInsights.Extensibility.ITelemetryInitializer,
    BlinkMark.Api.Observability.QueryStringRedactingInitializer>();

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

// Container Apps terminates TLS at the ingress, so the scheme has to come from the forwarded
// headers or every generated URL would be http.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseBlinkMarkExceptionHandling();

app.UseHsts();

app.Use(async (context, next) =>
{
    // The API returns JSON only. These headers cost nothing and close off the class of attacks
    // that depend on a browser deciding to treat a response as something it is not.
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
    // The document itself is public — it describes the shape of the API, not its contents — but
    // the interactive UI is not shipped to production.
    app.UseSwagger();
}

// -----------------------------------------------------------------------------
// Health
//
// Unauthenticated by necessity: the Container Apps probe and the availability test cannot hold
// a credential. It returns nothing but liveness, so there is nothing here to protect.
// -----------------------------------------------------------------------------

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous()
    .ExcludeFromDescription();

app.MapGet("/health/ready", async (IServiceProvider services, CancellationToken cancellationToken) =>
    {
        var cosmos = services.GetRequiredService<Microsoft.Azure.Cosmos.CosmosClient>();

        try
        {
            await cosmos.ReadAccountAsync();
            return Results.Ok(new { status = "ready" });
        }
        catch (Exception)
        {
            // No detail. A readiness probe that names the failing dependency is a reconnaissance
            // endpoint for anyone who can reach it.
            return Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    })
    .AllowAnonymous()
    .ExcludeFromDescription();

// -----------------------------------------------------------------------------
// Endpoints
// -----------------------------------------------------------------------------

app.MapFileEndpoints();
app.MapCommentEndpoints();
app.MapRetentionEndpoints();

await app.RunAsync();

namespace BlinkMark.Api
{
    /// <summary>
    /// Marker type naming this assembly for <c>WebApplicationFactory</c>.
    /// </summary>
    /// <remarks>
    /// The compiler-generated <c>Program</c> from top-level statements lands in the global
    /// namespace, and the preview origin has one too. A test assembly that hosts both would not
    /// be able to name either. A distinct marker per host avoids that entirely.
    /// </remarks>
    public sealed class ApiHostMarker;
}
