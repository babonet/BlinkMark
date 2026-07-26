using BlinkMark.Api;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------------
// Configuration (T024)
//
// Environment variables only, supplied by container-apps.bicep. Every value is an endpoint or
// an identifier - there is no connection string, no account key, and no client secret, because
// every resource has local authentication disabled and would refuse one (Principle VII).
// -----------------------------------------------------------------------------

builder.Configuration.AddEnvironmentVariables();

// Service registration and the request pipeline both live in ApiHost, so that the local
// development host runs identical wiring rather than a divergent copy of it.
ApiHost.ConfigureServices(builder);

var app = builder.Build();

ApiHost.ConfigurePipeline(app);

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
