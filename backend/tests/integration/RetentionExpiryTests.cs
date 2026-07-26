using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlinkMark.Core.Abstractions;
using BlinkMark.TestHost;

namespace BlinkMark.IntegrationTests;

/// <summary>
/// Retention: the 24-hour default, the 30-day ceiling, and physical deletion (T033).
/// Mandatory under Principle VI.
/// </summary>
/// <remarks>
/// The assertion that matters most is the last one. It is not enough for the API to stop
/// admitting a file exists — SC-006 and the whole compliance premise require the bytes to be
/// gone. A file hidden from the API but still sitting in blob storage is a file that has not been
/// deleted, however the interface describes it.
/// </remarks>
public sealed class RetentionExpiryTests : IClassFixture<BlinkMarkApiFactory>
{
    private readonly BlinkMarkApiFactory _factory;

    public RetentionExpiryTests(BlinkMarkApiFactory factory) => _factory = factory;

    private HttpClient CreateClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Tokens.IssueUserToken(userId, $"User {userId}"));
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task A_new_file_expires_24_hours_after_upload()
    {
        var client = CreateClient("retention-default");

        var response = await client.PostAsync("/api/files", FileContent("draft.md", "Body."));
        var body = await ReadJsonAsync(response);

        var uploadedAt = body.GetProperty("uploadedAt").GetDateTimeOffset();
        var expiresAt = body.GetProperty("expiresAt").GetDateTimeOffset();

        Assert.Equal(TimeSpan.FromHours(24), expiresAt - uploadedAt);
    }

    [Fact]
    public async Task The_ceiling_is_stored_as_30_days_from_upload()
    {
        var client = CreateClient("retention-ceiling");

        var response = await client.PostAsync("/api/files", FileContent("draft.md", "Body."));
        var body = await ReadJsonAsync(response);

        var uploadedAt = body.GetProperty("uploadedAt").GetDateTimeOffset();
        var maxExpiresAt = body.GetProperty("maxExpiresAt").GetDateTimeOffset();

        // Stored, not recomputed. A ceiling derived from "now" at extension time would let a file
        // be extended indefinitely, 30 days at a time (FR-028).
        Assert.Equal(TimeSpan.FromDays(30), maxExpiresAt - uploadedAt);
    }

    [Fact]
    public async Task The_upload_response_says_when_the_file_dies_and_that_there_is_no_backup()
    {
        var client = CreateClient("retention-notice");

        var response = await client.PostAsync("/api/files", FileContent("draft.md", "Body."));
        var body = await ReadJsonAsync(response);

        var notice = body.GetProperty("retentionNotice").GetString();

        // FR-075. Download is the only way anyone preserves a review, so the absence of a backup
        // has to be said at upload rather than discovered after a loss.
        Assert.Contains("no backup", notice!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("30 days", notice!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_upload_response_says_who_can_see_the_file()
    {
        var client = CreateClient("retention-scope");

        var response = await client.PostAsync("/api/files", FileContent("draft.md", "Body."));
        var body = await ReadJsonAsync(response);

        var notice = body.GetProperty("accessScopeNotice").GetString();

        // SC-016. Clarification Q1 accepted that the link is the access grant; disclosure at
        // upload time is the mitigation, so its absence would be a real regression.
        Assert.Contains("link", notice!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("organization", notice!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_expired_file_reads_as_deleted_before_anything_is_swept()
    {
        var client = CreateClient("retention-reads-gone");

        var created = await client.PostAsync("/api/files", FileContent("draft.md", "Body."));
        var fileId = (await ReadJsonAsync(created)).GetProperty("id").GetString()!;

        _factory.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        // Nothing has run. The reconciliation job has not fired and the platform has not swept.
        // FR-032 still requires the file to read as gone the instant it expires.
        var response = await client.GetAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var content = await client.GetAsync($"/api/files/{fileId}/content");
        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
    }

    [Fact]
    public async Task An_expired_file_disappears_from_the_owners_list()
    {
        var client = CreateClient("retention-list");

        await client.PostAsync("/api/files", FileContent("draft.md", "Body."));

        var before = await ReadJsonAsync(await client.GetAsync("/api/files"));
        Assert.NotEmpty(before.GetProperty("files").EnumerateArray());

        _factory.Clock.Advance(TimeSpan.FromHours(25));

        var after = await ReadJsonAsync(await client.GetAsync("/api/files"));
        Assert.Empty(after.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task Expired_quota_capacity_frees_itself()
    {
        var client = CreateClient("retention-quota");

        await client.PostAsync("/api/files", FileContent("one.md", "Body."));
        await client.PostAsync("/api/files", FileContent("two.md", "Body."));

        var before = await ReadJsonAsync(await client.GetAsync("/api/files"));
        Assert.Equal(2, before.GetProperty("quota").GetProperty("liveFiles").GetInt32());

        _factory.Clock.Advance(TimeSpan.FromHours(25));

        var after = await ReadJsonAsync(await client.GetAsync("/api/files"));

        // FR-088 needs no implementation: the quota is a live count, and expiry reduces it. That
        // matters because clarification Q1 left nobody able to unblock a user who hits the cap.
        Assert.Equal(0, after.GetProperty("quota").GetProperty("liveFiles").GetInt32());
    }

    [Fact]
    public async Task Deleting_a_file_removes_its_blobs_physically()
    {
        var client = CreateClient("retention-physical");

        var created = await client.PostAsync("/api/files", FileContent("draft.md", "Body."));
        var body = await ReadJsonAsync(created);
        var fileId = body.GetProperty("id").GetString()!;

        Assert.True(_factory.Blobs.PhysicallyExists(fileId, BlobArtifact.Original));

        await client.DeleteAsync($"/api/files/{fileId}");

        // Physically, not just "reads as absent". The distinction is the whole of SC-006.
        Assert.False(_factory.Blobs.PhysicallyExists(fileId, BlobArtifact.Original));
    }

    [Fact]
    public async Task Blob_artifacts_are_written_with_an_expiry_that_matches_the_file()
    {
        var client = CreateClient("retention-blob-expiry");

        var created = await client.PostAsync("/api/files", FileContent("draft.md", "Body."));
        var fileId = (await ReadJsonAsync(created)).GetProperty("id").GetString()!;

        _factory.Clock.Advance(TimeSpan.FromHours(25));

        // The fake models the platform's own behaviour: expiry is set on the object, so the
        // bytes go whether or not any BlinkMark process is running (Principle II).
        _factory.Blobs.RunPlatformExpirySweep();

        Assert.False(_factory.Blobs.PhysicallyExists(fileId, BlobArtifact.Original));
    }

    [Fact]
    public async Task The_audit_entry_outlives_the_file_it_describes()
    {
        var client = CreateClient("retention-audit");

        var created = await client.PostAsync("/api/files", FileContent("draft.md", "Body."));
        var fileId = (await ReadJsonAsync(created)).GetProperty("id").GetString()!;

        await client.DeleteAsync($"/api/files/{fileId}");

        // FR-043. Deleting a file removes its blobs, its document, and its comments — and never
        // its audit entries. That asymmetry is deliberate and is what makes the trail useful.
        var entries = _factory.Audit.ForTarget(fileId).ToList();

        Assert.Contains(entries, entry => entry.Action == Core.Models.AuditAction.Upload);
        Assert.Contains(entries, entry => entry.Action == Core.Models.AuditAction.FileDelete);
    }
}
