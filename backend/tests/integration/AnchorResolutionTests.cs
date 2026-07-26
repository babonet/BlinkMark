using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlinkMark.Core.Anchoring;
using BlinkMark.Core.Models;
using BlinkMark.Core.Rendering;
using BlinkMark.TestHost;

namespace BlinkMark.IntegrationTests;

/// <summary>
/// Anchor resolution and orphaning (T052). Mandatory under Principle VI.
/// </summary>
/// <remarks>
/// Principle III is unusual in that its failure modes are worse than its absence. A comment that
/// silently binds to a different passage looks correct and is wrong; a comment that disappears
/// takes someone's review work with it. Both are more damaging than an honest "this no longer
/// resolves", which is what these tests are really checking for.
/// </remarks>
public sealed class AnchorResolutionTests(BlinkMarkApiFactory factory)
    : IClassFixture<BlinkMarkApiFactory>
{
    private readonly BlinkMarkApiFactory _factory = factory;

    private const string DuplicateMarkdown =
        "The duplicate passage problem appears twice.\n\n"
        + "Some intervening text so the two are not adjacent.\n\n"
        + "The duplicate passage problem appears twice.\n";

    private HttpClient CreateClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Tokens.IssueUserToken(userId, $"User {userId}"));
        return client;
    }

    private static MultipartFormDataContent FileContent(string fileName, string content)
    {
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(bytes, "file", fileName);
        return form;
    }

    private async Task<string> CreateFileAsync(HttpClient client, string markdown)
    {
        var created = await client.PostAsync("/api/files", FileContent("draft.md", markdown));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    // ---------------------------------------------------------------------
    // Domain-level resolution
    // ---------------------------------------------------------------------

    private static readonly AnchorService Anchors = new();

    private static string Projection(string markdown) =>
        new RenderPipeline(new MarkdownRenderer(), new HtmlSanitizerService())
            .Render(markdown, FileContentType.Markdown)
            .TextProjection;

    [Fact]
    public void A_duplicated_passage_resolves_to_the_occurrence_that_was_selected()
    {
        var projection = Projection(DuplicateMarkdown);
        var second = projection.LastIndexOf("The duplicate passage problem", StringComparison.Ordinal);

        var anchor = Anchors.CreateTextAnchor(projection, second, second + 29, "test-1");
        var resolution = Anchors.Resolve(anchor, projection);

        // The listed edge case. Without prefix/suffix context and the position hint, this would
        // resolve to the first occurrence and quietly move the comment to a different paragraph.
        Assert.True(resolution.IsResolved);
        Assert.Equal(second, resolution.Start);
    }

    [Fact]
    public void A_removed_passage_orphans_rather_than_binding_elsewhere()
    {
        var projection = Projection(DuplicateMarkdown);
        var anchor = new Anchor
        {
            Kind = AnchorKind.Text,
            Exact = "A sentence that is not in this document at all.",
            Prefix = "context ",
            Suffix = " context",
            Start = 10,
            End = 57,
            RenderVersion = "test-1",
        };

        var resolution = Anchors.Resolve(anchor, projection);

        Assert.False(resolution.IsResolved);
        Assert.Equal(AnchorState.Orphaned, resolution.State);
    }

    [Fact]
    public void An_orphaned_anchor_still_carries_the_quoted_passage()
    {
        var projection = Projection(DuplicateMarkdown);
        var start = projection.IndexOf("duplicate passage", StringComparison.Ordinal);

        var anchor = Anchors.CreateTextAnchor(projection, start, start + 17, "test-1");

        // FR-022: an orphaned comment shows what it was about. That is only possible because the
        // quote was stored at creation — it cannot be recovered afterwards.
        Assert.Equal("duplicate passage", anchor.Exact);
        Assert.NotEmpty(anchor.Prefix);
    }

    [Fact]
    public void A_render_version_change_is_detectable()
    {
        var projection = Projection(DuplicateMarkdown);
        var anchor = Anchors.CreateTextAnchor(projection, 0, 13, "sanitizer-1+renderer-1");

        // Anchors resolve against the sanitized render, so a sanitizer upgrade changes the text
        // underneath them. This is why renderVersion is pinned per file (research.md R2).
        Assert.True(OrphanDetector.IsStale(anchor, "sanitizer-2+renderer-1"));
        Assert.False(OrphanDetector.IsStale(anchor, "sanitizer-1+renderer-1"));
    }

    // ---------------------------------------------------------------------
    // End-to-end through the API
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_comment_on_real_text_is_stored_anchored()
    {
        var client = CreateClient("anchor-ok");
        var fileId = await CreateFileAsync(client, "# Report\n\nThe ingest path is the bottleneck.");

        var response = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new
            {
                body = "Agreed.",
                anchor = new
                {
                    kind = "text",
                    exact = "ingest path",
                    prefix = "The ",
                    suffix = " is the",
                    start = 0,
                    end = 11,
                },
            });

        var comment = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("anchored", comment.GetProperty("anchorState").GetString());
    }

    [Fact]
    public async Task A_comment_quoting_text_that_is_not_there_is_stored_orphaned_and_still_returned()
    {
        var client = CreateClient("anchor-orphan");
        var fileId = await CreateFileAsync(client, "# Report\n\nThe ingest path is the bottleneck.");

        var response = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new
            {
                body = "About a passage that does not exist.",
                anchor = new
                {
                    kind = "text",
                    exact = "a passage that was never in this document",
                    prefix = string.Empty,
                    suffix = string.Empty,
                    start = 0,
                    end = 41,
                },
            });

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("orphaned", created.GetProperty("anchorState").GetString());

        var listed = await (await client.GetAsync($"/api/files/{fileId}/comments"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var comment = listed.EnumerateArray().Single();

        // Visible, not hidden, and still carrying its quote so a reader can see what it meant.
        Assert.Equal("orphaned", comment.GetProperty("anchorState").GetString());
        Assert.Equal("About a passage that does not exist.", comment.GetProperty("body").GetString());
        Assert.Equal(
            "a passage that was never in this document",
            comment.GetProperty("anchor").GetProperty("exact").GetString());
    }

    [Fact]
    public async Task The_render_version_on_a_stored_anchor_comes_from_the_server()
    {
        var client = CreateClient("anchor-version");
        var fileId = await CreateFileAsync(client, "# Report\n\nThe ingest path is the bottleneck.");

        var response = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new
            {
                body = "Anchored.",
                anchor = new
                {
                    kind = "text",
                    exact = "ingest path",
                    prefix = string.Empty,
                    suffix = string.Empty,
                    start = 0,
                    end = 11,
                    renderVersion = "a-version-the-client-made-up",
                },
            });

        var comment = await response.Content.ReadFromJsonAsync<JsonElement>();

        // A client-supplied render version would make the anchor resolve against the wrong text.
        Assert.Equal(
            RenderPipeline.CurrentVersion,
            comment.GetProperty("anchor").GetProperty("renderVersion").GetString());
    }

    [Fact]
    public async Task Comments_survive_a_reload_in_a_different_session()
    {
        var author = CreateClient("anchor-author");
        var fileId = await CreateFileAsync(author, "# Report\n\nThe ingest path is the bottleneck.");

        await author.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new
            {
                body = "Still here later.",
                anchor = new
                {
                    kind = "text",
                    exact = "bottleneck",
                    prefix = "is the ",
                    suffix = ".",
                    start = 24,
                    end = 34,
                },
            });

        // A different colleague, a different session, same passage.
        var colleague = CreateClient("anchor-reader");

        var listed = await (await colleague.GetAsync($"/api/files/{fileId}/comments"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var comment = listed.EnumerateArray().Single();
        Assert.Equal("anchored", comment.GetProperty("anchorState").GetString());
        Assert.Equal("bottleneck", comment.GetProperty("anchor").GetProperty("exact").GetString());
    }

    [Fact]
    public async Task Comment_creation_writes_an_audit_entry_and_a_notification()
    {
        var client = CreateClient("anchor-audit");
        var fileId = await CreateFileAsync(client, "# Report\n\nThe ingest path is the bottleneck.");

        var response = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new
            {
                body = "Audited.",
                anchor = new { kind = "text", exact = "ingest path", prefix = "", suffix = "", start = 0, end = 11 },
            });

        var commentId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.Contains(
            _factory.Audit.For(AuditAction.CommentCreate),
            entry => entry.TargetId == commentId);

        // Enqueued, not sent. The comment path's only synchronous notification work is the
        // enqueue (FR-037).
        Assert.Contains(_factory.NotificationQueue.Enqueued, message => message.CommentId == commentId);
    }
}
