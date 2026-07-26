using BlinkMark.Core.Abstractions;
using BlinkMark.TestHost.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BlinkMark.TestHost;

/// <summary>
/// Hosts the preview origin in-process (T028).
/// </summary>
/// <remarks>
/// Constructed separately from the API on purpose. Sharing a host between the two would make it
/// impossible to prove the property that matters most about this service: that it has no session,
/// no Entra handler, and no credential other than a preview token.
/// <para>
/// The stores are injectable so a test can arrange a render artifact here and a token minted by
/// the API factory there, exercising the two sides of the contract independently.
/// </para>
/// </remarks>
public sealed class BlinkMarkPreviewFactory : WebApplicationFactory<BlinkMark.Preview.PreviewHostMarker>
{
    public BlinkMarkPreviewFactory(
        TestClock? clock = null,
        InMemoryBlobFileStore? blobs = null,
        InMemoryAuditStore? audit = null,
        TestTokenSigner? signer = null)
    {
        Clock = clock ?? new TestClock();
        Blobs = blobs ?? new InMemoryBlobFileStore(Clock);
        Audit = audit ?? new InMemoryAuditStore();
        Signer = signer ?? new TestTokenSigner();
    }

    public TestClock Clock { get; }

    public InMemoryBlobFileStore Blobs { get; }

    public InMemoryAuditStore Audit { get; }

    public TestTokenSigner Signer { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.UseSetting("BlinkMark:Preview:Origin", BlinkMarkApiFactory.PreviewOrigin);
        builder.UseSetting("BlinkMark:Preview:IssuerOrigin", BlinkMarkApiFactory.ApiOrigin);
        builder.UseSetting("BlinkMark:Preview:FrameAncestor", "https://app.blinkmark.test");
        builder.UseSetting("BlinkMark:Cosmos:Endpoint", "https://cosmos.invalid/");
        builder.UseSetting("BlinkMark:Blob:ServiceUri", "https://blob.invalid/");
        builder.UseSetting("BlinkMark:Audit:TableServiceUri", "https://table.invalid/");
        builder.UseSetting("BlinkMark:Notifications:QueueServiceUri", "https://queue.invalid/");
        builder.UseSetting("BlinkMark:KeyVault:Uri", "https://vault.invalid/");

        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IClock>(Clock));
            services.Replace(ServiceDescriptor.Singleton<IBlobFileStore>(Blobs));
            services.Replace(ServiceDescriptor.Singleton<IAuditStore>(Audit));
            services.Replace(ServiceDescriptor.Singleton<ITokenSigner>(Signer));
        });
    }
}
