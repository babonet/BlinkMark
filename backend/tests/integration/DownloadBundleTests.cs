using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BlinkMark.TestHost;

namespace BlinkMark.IntegrationTests;

/// <summary>
/// Download bundle completeness (T069, FR-031). Mandatory under Principle VI.
/// </summary>
/// <remarks>
/// The download is the only way review work survives expiry, so an incomplete bundle is silent
/// data loss dressed up as a successful operation — precisely what Principle III forbids. The
/// tests below therefore care much less about the archive format than about what is missing from
/// it, and the orphaned-comment case is the one most likely to regress: an orphan is easy to
/// filter out as "broken", and filtering it out destroys the only surviving record of what the
/// deleted passage said.
/// </remarks>
public sealed class DownloadBundleTests : IClassFixture<BlinkMarkApiFactory>
{
    private readonly BlinkMarkApiFactory _factory;

    public DownloadBundleTests(BlinkMarkApiFactory factory) => _factory = factory;

    private HttpClient CreateClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Tokens.IssueUserToken(userId, $"User {userId}"));
        return client;
    }

    private const string Body = "The ingest path is the bottleneck.\n\nRetention is enforced by the platform.";

    private async Task<JsonElement> UploadAsync(HttpClient client)
    {
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(Body));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(bytes, "file", "draft.md");

        var response = await client.PostAsync("/api/files", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> CommentAsync(
        HttpClient client,
        string fileId,
        string body,
        string quote,
        string? parentId = null)
    {
        var start = Body.IndexOf(quote, StringComparison.Ordinal);

        return client.PostAsJsonAsync($"/api/files/{fileId}/comments", new
        {
            body,
            parentId,
            anchor = new
            {
                kind = "text",
                exact = quote,
                prefix = Body[Math.Max(0, start - 32)..start],
                suffix = Body[(start + quote.Length)..Math.Min(Body.Length, start + quote.Length + 32)],
                start,
                end = start + quote.Length,
            },
        });
    }

    private async Task<JsonElement> DownloadSidecarAsync(HttpClient client, string fileId)
    {
        var response = await client.GetAsync($"/api/files/{fileId}/download");
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var entry = archive.GetEntry("comments.json");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task The_bundle_contains_the_original_content_and_the_comment_sidecar()
    {
        var client = CreateClient("bundle-shape");
        var file = await UploadAsync(client);
        var fileId = file.GetProperty("id").GetString()!;

        var response = await client.GetAsync($"/api/files/{fileId}/download");
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Contains(archive.Entries, entry => entry.Name == "comments.json");
        Assert.Contains(archive.Entries, entry => entry.Name == "draft.md");

        // The original, not the sanitized render. The owner is getting their file back.
        var content = archive.Entries.Single(entry => entry.Name == "draft.md");
        using var reader = new StreamReader(content.Open(), Encoding.UTF8);
        Assert.Equal(Body, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Every_comment_carries_author_time_thread_and_anchored_passage()
    {
        var client = CreateClient("bundle-fields");
        var file = await UploadAsync(client);
        var fileId = file.GetProperty("id").GetString()!;

        await CommentAsync(client, fileId, "Can we quantify this?", "The ingest path is the bottleneck.");

        var sidecar = await DownloadSidecarAsync(client, fileId);
        var comment = sidecar.GetProperty("comments").EnumerateArray().Single();

        Assert.Equal("Can we quantify this?", comment.GetProperty("body").GetString());
        Assert.Equal("User bundle-fields", comment.GetProperty("author").GetString());
        Assert.False(string.IsNullOrWhiteSpace(comment.GetProperty("threadId").GetString()));
        Assert.True(comment.GetProperty("createdAt").GetDateTimeOffset() > DateTimeOffset.MinValue);

        // Without the passage, a downloaded comment says "can we quantify this?" about nothing.
        Assert.Equal(
            "The ingest path is the bottleneck.",
            comment.GetProperty("anchoredPassage").GetString());
    }

    [Fact]
    public async Task Replies_keep_their_thread_and_parent()
    {
        var client = CreateClient("bundle-threads");
        var file = await UploadAsync(client);
        var fileId = file.GetProperty("id").GetString()!;

        var root = await CommentAsync(client, fileId, "Root.", "Retention is enforced by the platform.");
        var rootBody = await root.Content.ReadFromJsonAsync<JsonElement>();
        var rootId = rootBody.GetProperty("id").GetString()!;

        await CommentAsync(client, fileId, "Reply.", "Retention is enforced by the platform.", rootId);

        var sidecar = await DownloadSidecarAsync(client, fileId);
        var comments = sidecar.GetProperty("comments").EnumerateArray().ToList();

        Assert.Equal(2, comments.Count);

        var reply = comments.Single(c => c.GetProperty("body").GetString() == "Reply.");
        Assert.Equal(rootId, reply.GetProperty("parentId").GetString());
        Assert.Equal(rootId, reply.GetProperty("threadId").GetString());
    }

    [Fact]
    public async Task Orphaned_comments_are_present_and_labelled()
    {
        // The requirement this suite exists for. An orphan is the comment most tempting to drop
        // and the one whose loss costs most, because the document no longer contains the text it
        // quotes.
        var client = CreateClient("bundle-orphan");
        var file = await UploadAsync(client);
        var fileId = file.GetProperty("id").GetString()!;

        await CommentAsync(client, fileId, "About the vanished line.", "The ingest path is the bottleneck.");

        // Rewrite the projection out from under the anchor.
        _factory.Blobs.OverwriteProjection(
            fileId,
            "An entirely different document with none of the original wording in it.");

        var sidecar = await DownloadSidecarAsync(client, fileId);
        var comment = sidecar.GetProperty("comments").EnumerateArray().Single();

        Assert.True(comment.GetProperty("isOrphaned").GetBoolean());
        Assert.Equal("About the vanished line.", comment.GetProperty("body").GetString());

        // The quoted passage is the only surviving record of the deleted text.
        Assert.Equal(
            "The ingest path is the bottleneck.",
            comment.GetProperty("anchoredPassage").GetString());

        Assert.Equal(1, sidecar.GetProperty("orphanedCommentCount").GetInt32());
    }

    [Fact]
    public async Task Deleted_comments_survive_as_tombstones_without_their_bodies()
    {
        var client = CreateClient("bundle-deleted");
        var file = await UploadAsync(client);
        var fileId = file.GetProperty("id").GetString()!;

        var created = await CommentAsync(client, fileId, "Withdrawn.", "Retention is enforced by the platform.");
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var commentId = createdBody.GetProperty("id").GetString()!;

        await CommentAsync(client, fileId, "Reply to it.", "Retention is enforced by the platform.", commentId);
        await client.DeleteAsync($"/api/files/{fileId}/comments/{commentId}");

        var sidecar = await DownloadSidecarAsync(client, fileId);
        var comments = sidecar.GetProperty("comments").EnumerateArray().ToList();

        // The tombstone stays so the reply is not left answering nothing...
        var tombstone = comments.Single(c => c.GetProperty("id").GetString() == commentId);
        Assert.True(tombstone.GetProperty("isDeleted").GetBoolean());

        // ...but the withdrawn text is not resurrected by the export.
        Assert.False(tombstone.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task The_bundle_says_the_downloaded_copy_outlives_expiry()
    {
        // FR-076. The notice is the control: the copy is no longer governed by BlinkMark, and the
        // person holding it needs to know that.
        var client = CreateClient("bundle-notice");
        var file = await UploadAsync(client);
        var fileId = file.GetProperty("id").GetString()!;

        var sidecar = await DownloadSidecarAsync(client, fileId);
        var notice = sidecar.GetProperty("notice").GetString();

        Assert.Contains("no longer governed by BlinkMark's retention", notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_owner_cannot_download()
    {
        var owner = CreateClient("bundle-owner");
        var file = await UploadAsync(owner);
        var fileId = file.GetProperty("id").GetString()!;

        var other = CreateClient("bundle-other");
        var response = await other.GetAsync($"/api/files/{fileId}/download");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_file_cannot_be_downloaded()
    {
        // The download is the only route by which content outlives its retention window, so this
        // is the one that would matter most if it regressed.
        //
        // Asserted as "denied, and denied indistinguishably from a file that never existed"
        // rather than as a specific status: owner-scoped routes resolve ownership before the
        // handler runs, and that handler treats expired and non-existent alike (FR-032).
        var client = CreateClient("bundle-expired");
        var file = await UploadAsync(client);
        var fileId = file.GetProperty("id").GetString()!;

        var before = _factory.Clock.UtcNow;
        _factory.Clock.Advance(TimeSpan.FromHours(25));

        try
        {
            var expired = await client.GetAsync($"/api/files/{fileId}/download");
            var neverExisted = await client.GetAsync("/api/files/01ARZ3NDEKTSV4RRFFQ69G5FAV/download");

            Assert.False(expired.IsSuccessStatusCode);
            Assert.Equal(neverExisted.StatusCode, expired.StatusCode);
        }
        finally
        {
            _factory.Clock.AdvanceTo(before);
        }
    }

    [Fact]
    public async Task A_download_is_audited()
    {
        var client = CreateClient("bundle-audit");
        var file = await UploadAsync(client);
        var fileId = file.GetProperty("id").GetString()!;

        await client.GetAsync($"/api/files/{fileId}/download");

        Assert.Contains(_factory.Audit.Entries, entry =>
            entry.Action == Core.Models.AuditAction.Download && entry.TargetId == fileId);
    }

    [Fact]
    public async Task A_hostile_display_name_cannot_write_outside_the_archive()
    {
        // The display name came from a user. If it reached the zip entry name unfiltered, a name
        // like "../../evil.md" would become a path-traversal entry in whatever tool the owner
        // extracts with.
        var client = CreateClient("bundle-traversal");

        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(Body));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(bytes, "file", "../../evil.md");

        var upload = await client.PostAsync("/api/files", form);
        upload.EnsureSuccessStatusCode();

        var fileId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await client.GetAsync($"/api/files/{fileId}/download");
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.All(archive.Entries, entry =>
        {
            Assert.DoesNotContain("..", entry.FullName, StringComparison.Ordinal);
            Assert.DoesNotContain("/", entry.FullName, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", entry.FullName, StringComparison.Ordinal);
        });
    }
}
