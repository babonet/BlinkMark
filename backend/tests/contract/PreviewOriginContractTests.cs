using System.Net;
using System.Text;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;
using BlinkMark.Core.Preview;
using BlinkMark.TestHost;
using BlinkMark.TestHost.Fakes;

namespace BlinkMark.ContractTests;

/// <summary>
/// Conformance of the preview origin to <c>contracts/preview-origin.md</c> (T119).
/// </summary>
/// <remarks>
/// This is the contract that closes the gap the consistency analysis found: isolating the preview
/// onto its own origin to satisfy Principle IV removed the session that Principle I depends on,
/// and nothing replaced it. These tests assert that the replacement actually holds — that the
/// token is required, that it is scoped, that it expires, and that a token minted for the API
/// does not open the preview host.
/// <para>
/// The reusability test is here for a different reason. A single-use token would break ordinary
/// back-and-forward navigation, so "reusable within its lifetime" is a requirement rather than a
/// concession, and a well-meaning hardening change could easily break it.
/// </para>
/// </remarks>
public sealed class PreviewOriginContractTests
{
    private const string FileId = "01JBQZK4T3N7XW9E2M6H0YV8QD";
    private const string RenderVersion = "htmlsanitizer-9.1+markdig-0.38";
    private const string RenderedHtml = "<h1>Rendered</h1><p>Sanitized content.</p>";

    private static (BlinkMarkPreviewFactory Factory, PreviewTokenService Tokens, TestClock Clock) CreateHost()
    {
        var clock = new TestClock();
        var blobs = new InMemoryBlobFileStore(clock);
        var audit = new InMemoryAuditStore();
        var signer = new TestTokenSigner();

        blobs.WriteAsync(
            FileId,
            BlobArtifact.Render,
            new MemoryStream(Encoding.UTF8.GetBytes(RenderedHtml)),
            "text/html",
            clock.UtcNow.AddDays(1),
            RenderVersion).GetAwaiter().GetResult();

        var factory = new BlinkMarkPreviewFactory(clock, blobs, audit, signer);

        var tokens = new PreviewTokenService(
            signer,
            clock,
            new PreviewTokenOptions
            {
                Issuer = BlinkMarkApiFactory.ApiOrigin,
                Audience = BlinkMarkApiFactory.PreviewOrigin,
            });

        return (factory, tokens, clock);
    }

    [Fact]
    public async Task Without_a_token_the_preview_is_refused_and_says_nothing()
    {
        var (factory, _, _) = CreateHost();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/p/{FileId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // No content, no filename, no confirmation the file exists (FR-001).
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
    }

    [Fact]
    public async Task With_a_valid_token_the_stored_render_is_returned()
    {
        var (factory, tokens, _) = CreateHost();
        using var client = factory.CreateClient();

        var token = await tokens.IssueAsync(FileId, RenderVersion, "user-1", "corr-1");

        var response = await client.GetAsync($"/p/{FileId}?t={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Sanitized content.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (factory, tokens, clock) = CreateHost();
        using var client = factory.CreateClient();

        var token = await tokens.IssueAsync(FileId, RenderVersion, "user-1", "corr-1");

        clock.Advance(TimeSpan.FromMinutes(16));

        var response = await client.GetAsync($"/p/{FileId}?t={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_minted_for_a_different_file_is_forbidden()
    {
        var (factory, tokens, _) = CreateHost();
        using var client = factory.CreateClient();

        var otherFileId = Identifiers.New();
        var token = await tokens.IssueAsync(otherFileId, RenderVersion, "user-1", "corr-1");

        var response = await client.GetAsync($"/p/{FileId}?t={Uri.EscapeDataString(token)}");

        // 403 rather than 401: the caller is authenticated, the grant simply does not cover this
        // file. One token never covers two files.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_token_minted_for_the_api_audience_is_refused()
    {
        var (factory, _, clock) = CreateHost();
        using var client = factory.CreateClient();

        var apiAudienceTokens = new PreviewTokenService(
            factory.Signer,
            clock,
            new PreviewTokenOptions
            {
                Issuer = BlinkMarkApiFactory.ApiOrigin,
                // Deliberately wrong: this is the API's own audience.
                Audience = BlinkMarkApiFactory.ApiAudience,
            });

        var token = await apiAudienceTokens.IssueAsync(FileId, RenderVersion, "user-1", "corr-1");

        var response = await client.GetAsync($"/p/{FileId}?t={Uri.EscapeDataString(token)}");

        // Without this, the isolation between the two origins would be cosmetic.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_is_reusable_within_its_lifetime()
    {
        var (factory, tokens, clock) = CreateHost();
        using var client = factory.CreateClient();

        var token = await tokens.IssueAsync(FileId, RenderVersion, "user-1", "corr-1");
        var url = $"/p/{FileId}?t={Uri.EscapeDataString(token)}";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(5));

        // Single-use would break back-navigation and tab restore. Reusability is a requirement,
        // not a concession.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_tampered_token_is_refused()
    {
        var (factory, tokens, _) = CreateHost();
        using var client = factory.CreateClient();

        var token = await tokens.IssueAsync(FileId, RenderVersion, "user-1", "corr-1");

        // Flip the last character of the signature.
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        var response = await client.GetAsync($"/p/{FileId}?t={Uri.EscapeDataString(tampered)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_response_carries_every_required_header()
    {
        var (factory, tokens, _) = CreateHost();
        using var client = factory.CreateClient();

        var token = await tokens.IssueAsync(FileId, RenderVersion, "user-1", "corr-1");
        var response = await client.GetAsync($"/p/{FileId}?t={Uri.EscapeDataString(token)}");

        var csp = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));

        // `default-src 'none'` means the render cannot reach the network at all, which enforces
        // FR-016 at the browser as well as at sanitization time.
        Assert.Contains("sandbox", csp, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors https://app.blinkmark.test", csp, StringComparison.Ordinal);

        // Without this the token in the URL would leak onward through navigation.
        Assert.Equal("no-referrer", string.Join(' ', response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("nosniff", string.Join(' ', response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control").First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redeemed_token_writes_a_preview_audit_entry()
    {
        var (factory, tokens, _) = CreateHost();
        using var client = factory.CreateClient();

        var token = await tokens.IssueAsync(FileId, RenderVersion, "user-preview", "corr-preview");
        await client.GetAsync($"/p/{FileId}?t={Uri.EscapeDataString(token)}");

        var entry = Assert.Single(factory.Audit.For(AuditAction.Preview));

        // Only the preview host can write this accurately: the API mints tokens but cannot know
        // whether one was ever redeemed.
        Assert.Equal("user-preview", entry.ActorId);
        Assert.Equal("corr-preview", entry.CorrelationId);
        Assert.Equal(FileId, entry.TargetId);
    }

    [Fact]
    public async Task An_unredeemed_token_writes_no_audit_entry()
    {
        var (factory, tokens, _) = CreateHost();

        await tokens.IssueAsync(FileId, RenderVersion, "user-unused", "corr-unused");

        Assert.Empty(factory.Audit.For(AuditAction.Preview));
    }

    [Fact]
    public async Task A_token_can_never_be_minted_for_longer_than_fifteen_minutes()
    {
        var clock = new TestClock();
        var signer = new TestTokenSigner();

        var overreaching = new PreviewTokenService(
            signer,
            clock,
            new PreviewTokenOptions
            {
                Issuer = BlinkMarkApiFactory.ApiOrigin,
                Audience = BlinkMarkApiFactory.PreviewOrigin,
                // Configuration asks for a day. The ceiling is not configurable.
                Lifetime = TimeSpan.FromDays(1),
            });

        var token = await overreaching.IssueAsync(FileId, RenderVersion, "user-1", "corr-1");

        clock.Advance(TimeSpan.FromMinutes(16));

        var result = await overreaching.ValidateAsync(token, FileId);

        Assert.False(result.IsValid);
        Assert.Equal(PreviewTokenFailure.Expired, result.Failure);
    }
}
