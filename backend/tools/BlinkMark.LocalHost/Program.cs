using BlinkMark.Core.Abstractions;
using BlinkMark.LocalHost;
using BlinkMark.TestHost;
using BlinkMark.TestHost.Fakes;
using Microsoft.Extensions.DependencyInjection.Extensions;

// -----------------------------------------------------------------------------
// BlinkMark, running on this machine, with no Azure and no Docker.
//
// What is real here: every endpoint, the authentication handler, every authorization policy, the
// sanitizer, the renderer, the anchoring algorithm, the retention rules, and the middleware
// pipeline — all reused from ApiHost, not reimplemented.
//
// What is not real: storage, which is in-memory and evaporates on exit; and the token signing
// key, which is symmetric and local instead of Entra's.
//
// Authentication is deliberately NOT bypassed. A stub handler would have been less code, but it
// would have made every authorization rule untestable by hand — the thing you most want to poke
// at locally is precisely the thing a bypass switches off. Instead this host mints genuine JWTs
// and lets the production handler validate them, so tenant pinning, audience pinning, expiry, and
// the app-only refusal in FR-055 all still run, and still reject.
// -----------------------------------------------------------------------------

var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var clock = new SystemClockAdapter();
var signer = new TestTokenSigner();
var tokens = new TestTokenIssuer(LocalConfiguration.TenantId, LocalConfiguration.ApiAudience, LocalConfiguration.SpaClientId);

// One store per port, shared between the API and the preview origin. The preview host reads the
// render the API wrote, so they must be looking at the same bytes.
var blobs = new InMemoryBlobFileStore(clock);
var audit = new InMemoryAuditStore();

var api = BuildApi();
var preview = BuildPreview();

LocalConfiguration.PrintBanner(tokens);

await Task.WhenAll(api.RunAsync(cancellation.Token), preview.RunAsync(cancellation.Token));

WebApplication BuildApi()
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Environment.EnvironmentName = Environments.Development;
    builder.Configuration.AddInMemoryCollection(LocalConfiguration.ApiSettings());
    builder.WebHost.UseUrls(LocalConfiguration.ApiOrigin);

    // The real wiring. Not a local approximation of it.
    BlinkMark.Api.ApiHost.ConfigureServices(builder);

    builder.Services.ReplaceWithInMemoryAdapters(clock, blobs, audit, signer);
    builder.Services.ConfigureTestJwtBearer();

    var app = builder.Build();

    BlinkMark.Api.ApiHost.ConfigurePipeline(app);
    app.MapLocalTokenEndpoint(tokens);

    return app;
}

WebApplication BuildPreview()
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Environment.EnvironmentName = Environments.Development;
    builder.Configuration.AddInMemoryCollection(LocalConfiguration.PreviewSettings());
    builder.WebHost.UseUrls(LocalConfiguration.PreviewOrigin);

    BlinkMark.Preview.PreviewHost.ConfigureServices(builder);

    builder.Services.ReplaceWithInMemoryAdapters(clock, blobs, audit, signer);

    var app = builder.Build();

    BlinkMark.Preview.PreviewHost.ConfigurePipeline(app);

    return app;
}

namespace BlinkMark.LocalHost
{
    /// <summary>The system clock, wrapped for the in-memory adapters that expect an IClock.</summary>
    internal sealed class SystemClockAdapter : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
