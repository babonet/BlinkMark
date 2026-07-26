using BlinkMark.Preview;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

// -----------------------------------------------------------------------------
// The preview origin (T026).
//
// This service exists on its own hostname so that a sanitizer bypass lands in an origin with no
// access to the application''s session, tokens, or cookies (Principle IV).
//
// Registration and the pipeline live in PreviewHost, so the local development host runs the same
// Content-Security-Policy this one does rather than a copy that can drift from it.
// -----------------------------------------------------------------------------

PreviewHost.ConfigureServices(builder);

var app = builder.Build();

PreviewHost.ConfigurePipeline(app);

await app.RunAsync();

namespace BlinkMark.Preview
{
    /// <summary>Marker type naming this assembly for <c>WebApplicationFactory</c>.</summary>
    public sealed class PreviewHostMarker;
}
