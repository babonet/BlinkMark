using System.Text;
using BlinkMark.Core.Anchoring;
using BlinkMark.Core.Models;
using BlinkMark.Core.Retention;
using BlinkMark.Core.Upload;

namespace BlinkMark.UnitTests;

/// <summary>Retention rules, in isolation from storage.</summary>
public sealed class RetentionPolicyTests
{
    private static readonly DateTimeOffset UploadedAt = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_default_lifetime_is_24_hours()
    {
        Assert.Equal(UploadedAt.AddHours(24), RetentionPolicy.DefaultExpiresAt(UploadedAt));
    }

    [Fact]
    public void The_ceiling_is_measured_from_upload_not_from_the_request()
    {
        // This is the property that makes indefinite extension impossible. A ceiling recomputed
        // from "now" at each extension would let a file live forever, 30 days at a time.
        Assert.Equal(UploadedAt.AddDays(30), RetentionPolicy.MaxExpiresAt(UploadedAt));
    }

    [Fact]
    public void An_expiry_beyond_the_ceiling_is_refused()
    {
        var result = RetentionPolicy.Validate(
            UploadedAt.AddDays(31),
            RetentionPolicy.MaxExpiresAt(UploadedAt),
            UploadedAt.AddDays(1));

        Assert.False(result.IsValid);
        Assert.Equal(RetentionViolation.ExceedsCeiling, result.Violation);
    }

    [Fact]
    public void An_expiry_in_the_past_is_refused()
    {
        var result = RetentionPolicy.Validate(
            UploadedAt.AddHours(1),
            RetentionPolicy.MaxExpiresAt(UploadedAt),
            UploadedAt.AddHours(2));

        Assert.False(result.IsValid);
        Assert.Equal(RetentionViolation.InThePast, result.Violation);
    }

    [Fact]
    public void An_expiry_exactly_on_the_ceiling_is_allowed()
    {
        var ceiling = RetentionPolicy.MaxExpiresAt(UploadedAt);

        var result = RetentionPolicy.Validate(ceiling, ceiling, UploadedAt.AddDays(1));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void The_ttl_never_drops_to_zero_or_below()
    {
        // Cosmos rejects a non-positive TTL, so an already-expired item has to be given the
        // shortest legal life rather than refused outright.
        Assert.Equal(1, RetentionPolicy.TtlSeconds(UploadedAt, UploadedAt.AddHours(1)));
    }
}

/// <summary>Upload validation, including the extension-versus-content check.</summary>
public sealed class UploadValidatorTests
{
    private readonly UploadValidator _validator = new(maxSizeBytes: 1024);

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Markdown_content_in_a_markdown_file_is_accepted()
    {
        var result = _validator.Validate("notes.md", Content("# Heading\n\nBody."));

        Assert.True(result.IsValid);
        Assert.Equal(FileContentType.Markdown, result.ContentType);
    }

    [Fact]
    public void Html_content_in_an_html_file_is_accepted()
    {
        var result = _validator.Validate("page.html", Content("<!doctype html><html><body><p>Hi</p></body></html>"));

        Assert.True(result.IsValid);
        Assert.Equal(FileContentType.Html, result.ContentType);
    }

    [Fact]
    public void Html_content_in_a_markdown_file_is_refused_rather_than_reclassified()
    {
        // The two formats take different rendering paths — Markdown disables raw HTML, HTML does
        // not — so guessing which the uploader meant would be a security decision made on their
        // behalf. Naming the disagreement is the only honest answer (FR-007).
        var result = _validator.Validate("sneaky.md", Content("<!doctype html><html><body><script>x</script></body></html>"));

        Assert.False(result.IsValid);
        Assert.Equal(UploadRejection.ContentDoesNotMatchExtension, result.Rejection);
    }

    [Fact]
    public void Markdown_containing_a_stray_inline_tag_is_still_markdown()
    {
        // Markdown legitimately contains occasional inline HTML. Only document structure — a
        // doctype, or html/head/body — makes something an HTML document.
        var result = _validator.Validate("notes.md", Content("Some text with a <br> in it.\n\nMore text."));

        Assert.True(result.IsValid);
        Assert.Equal(FileContentType.Markdown, result.ContentType);
    }

    [Fact]
    public void An_unsupported_extension_is_refused()
    {
        var result = _validator.Validate("payload.exe", Content("MZ"));

        Assert.False(result.IsValid);
        Assert.Equal(UploadRejection.UnsupportedExtension, result.Rejection);
    }

    [Fact]
    public void A_file_over_the_size_limit_is_refused()
    {
        var result = _validator.Validate("big.md", Content(new string('a', 2048)));

        Assert.False(result.IsValid);
        Assert.Equal(UploadRejection.TooLarge, result.Rejection);
    }

    [Fact]
    public void A_binary_file_is_refused_even_with_a_permitted_extension()
    {
        var result = _validator.Validate("notes.md", new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04]));

        Assert.False(result.IsValid);
        Assert.Equal(UploadRejection.NotText, result.Rejection);
    }
}

/// <summary>Anchor computation and resolution.</summary>
public sealed class AnchorServiceTests
{
    private const string Projection =
        "The ingest path is the bottleneck.\n"
        + "Retention is enforced by the platform.\n"
        + "The ingest path is the bottleneck.\n"
        + "The audit trail outlives the content.";

    private const string RenderVersion = "test-1";

    private readonly AnchorService _anchors = new();

    [Fact]
    public void A_unique_passage_resolves_exactly()
    {
        var start = Projection.IndexOf("Retention is enforced", StringComparison.Ordinal);
        var anchor = _anchors.CreateTextAnchor(Projection, start, start + 21, RenderVersion);

        var resolution = _anchors.Resolve(anchor, Projection);

        Assert.Equal(AnchorMatchQuality.Exact, resolution.Quality);
        Assert.Equal(start, resolution.Start);
    }

    [Fact]
    public void A_duplicated_passage_is_disambiguated_by_its_surrounding_context()
    {
        // The listed edge case. Two identical sentences, and the anchor must come back to the
        // second one — not to whichever the search happened to find first.
        var second = Projection.LastIndexOf("The ingest path is the bottleneck.", StringComparison.Ordinal);
        var anchor = _anchors.CreateTextAnchor(Projection, second, second + 34, RenderVersion);

        var resolution = _anchors.Resolve(anchor, Projection);

        Assert.True(resolution.IsResolved);
        Assert.Equal(second, resolution.Start);
    }

    [Fact]
    public void A_passage_that_no_longer_exists_orphans_rather_than_moving()
    {
        var anchor = new Anchor
        {
            Kind = AnchorKind.Text,
            Exact = "A sentence that was deleted entirely from the document.",
            Prefix = "context before ",
            Suffix = " context after",
            Start = 10,
            End = 64,
            RenderVersion = RenderVersion,
        };

        var resolution = _anchors.Resolve(anchor, Projection);

        // Principle III forbids the two alternatives: silently binding to different text, and
        // hiding the comment. Both destroy review work, and the first does it invisibly.
        Assert.False(resolution.IsResolved);
        Assert.Equal(AnchorState.Orphaned, resolution.State);
    }

    [Fact]
    public void A_lightly_edited_passage_still_resolves_approximately()
    {
        var start = Projection.IndexOf("The audit trail outlives", StringComparison.Ordinal);
        var anchor = _anchors.CreateTextAnchor(Projection, start, start + 24, RenderVersion);

        var drifted = Projection.Replace("audit trail outlives", "audit trail outlasts", StringComparison.Ordinal);

        var resolution = _anchors.Resolve(anchor, drifted);

        Assert.True(resolution.IsResolved);
        Assert.Equal(AnchorMatchQuality.Approximate, resolution.Quality);
    }

    [Fact]
    public void An_anchor_stores_the_quoted_passage_so_an_orphan_has_something_to_show()
    {
        var start = Projection.IndexOf("Retention", StringComparison.Ordinal);
        var anchor = _anchors.CreateTextAnchor(Projection, start, start + 9, RenderVersion);

        // FR-022: an orphaned comment displays this. Recomputing it later would be impossible,
        // which is exactly why it is stored rather than derived.
        Assert.Equal("Retention", anchor.Exact);
        Assert.NotEmpty(anchor.Prefix);
        Assert.NotEmpty(anchor.Suffix);
    }
}
