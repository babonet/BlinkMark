using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BlinkMark.Core.Retention;
using BlinkMark.TestHost;

namespace BlinkMark.IntegrationTests;

/// <summary>
/// The 30-day ceiling, and that it holds on every interface (T068, FR-028, FR-029).
/// Mandatory under Principle VI.
/// </summary>
/// <remarks>
/// The point of this suite is not that the retention endpoint validates its input — that would be
/// a unit test. It is that the ceiling cannot be got around: not by extending repeatedly, not by
/// a non-owner, not by writing through a different code path, and not by extending a file that
/// has already expired. Principle II calls the 30-day ceiling absolute, and "absolute" is a claim
/// about every path into the data, not about one handler.
/// </remarks>
public sealed class RetentionCeilingTests : IClassFixture<BlinkMarkApiFactory>
{
    private readonly BlinkMarkApiFactory _factory;

    public RetentionCeilingTests(BlinkMarkApiFactory factory) => _factory = factory;

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
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(bytes, "file", fileName);
        return form;
    }

    private async Task<JsonElement> UploadAsync(HttpClient client, string name = "draft.md")
    {
        var response = await client.PostAsync("/api/files", FileContent(name, "# Draft\n\nBody."));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> ExtendAsync(
        HttpClient client,
        string fileId,
        DateTimeOffset expiresAt) =>
        client.PatchAsJsonAsync($"/api/files/{fileId}/retention", new { expiresAt });

    [Fact]
    public async Task An_owner_can_extend_within_the_ceiling()
    {
        var client = CreateClient("ceiling-owner");
        var file = await UploadAsync(client);

        var fileId = file.GetProperty("id").GetString()!;
        var previous = file.GetProperty("expiresAt").GetDateTimeOffset();
        var target = file.GetProperty("uploadedAt").GetDateTimeOffset().AddDays(7);

        var response = await ExtendAsync(client, fileId, target);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(target, body.GetProperty("expiresAt").GetDateTimeOffset());

        // The old expiry comes back too, so the owner can see what actually changed.
        Assert.Equal(previous, body.GetProperty("previousExpiresAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task The_extension_survives_a_reread()
    {
        // Returning the new expiry proves the handler computed it. Reading it back proves it was
        // stored, which is the part that governs deletion.
        var client = CreateClient("ceiling-persist");
        var file = await UploadAsync(client);

        var fileId = file.GetProperty("id").GetString()!;
        var target = file.GetProperty("uploadedAt").GetDateTimeOffset().AddDays(3);

        await ExtendAsync(client, fileId, target);

        var reread = await client.GetFromJsonAsync<JsonElement>($"/api/files/{fileId}");
        Assert.Equal(target, reread.GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Extending_past_30_days_from_upload_is_refused()
    {
        var client = CreateClient("ceiling-exceed");
        var file = await UploadAsync(client);

        var fileId = file.GetProperty("id").GetString()!;
        var uploadedAt = file.GetProperty("uploadedAt").GetDateTimeOffset();

        var response = await ExtendAsync(client, fileId, uploadedAt.AddDays(30).AddMinutes(1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task The_ceiling_is_measured_from_upload_not_from_the_request()
    {
        // The failure this guards against: recomputing the ceiling as "now + 30 days" on each
        // extension, which lets a file be kept alive forever in 30-day hops. Two successive
        // extensions must not move the ceiling.
        var client = CreateClient("ceiling-ratchet");
        var file = await UploadAsync(client);

        var fileId = file.GetProperty("id").GetString()!;
        var uploadedAt = file.GetProperty("uploadedAt").GetDateTimeOffset();
        var ceiling = file.GetProperty("maxExpiresAt").GetDateTimeOffset();

        Assert.Equal(uploadedAt.AddDays(30), ceiling);

        await ExtendAsync(client, fileId, uploadedAt.AddDays(29));

        var reread = await client.GetFromJsonAsync<JsonElement>($"/api/files/{fileId}");
        Assert.Equal(ceiling, reread.GetProperty("maxExpiresAt").GetDateTimeOffset());

        var second = await ExtendAsync(client, fileId, ceiling.AddDays(1));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    [Fact]
    public async Task Extending_exactly_to_the_ceiling_is_allowed()
    {
        // The boundary belongs to the owner. An off-by-one here would quietly cost a day.
        var client = CreateClient("ceiling-boundary");
        var file = await UploadAsync(client);

        var fileId = file.GetProperty("id").GetString()!;
        var ceiling = file.GetProperty("maxExpiresAt").GetDateTimeOffset();

        var response = await ExtendAsync(client, fileId, ceiling);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Setting_an_expiry_in_the_past_is_refused()
    {
        var client = CreateClient("ceiling-past");
        var file = await UploadAsync(client);

        var fileId = file.GetProperty("id").GetString()!;
        var response = await ExtendAsync(client, fileId, _factory.Clock.UtcNow.AddMinutes(-1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_non_owner_cannot_extend_someone_elses_file()
    {
        var owner = CreateClient("ceiling-real-owner");
        var file = await UploadAsync(owner);
        var fileId = file.GetProperty("id").GetString()!;

        var other = CreateClient("ceiling-interloper");
        var response = await ExtendAsync(other, fileId, _factory.Clock.UtcNow.AddDays(2));

        // FR-029. A viewer can read the file; lifetime is the owner's alone.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_non_owner_cannot_delete_someone_elses_file()
    {
        var owner = CreateClient("ceiling-del-owner");
        var file = await UploadAsync(owner);
        var fileId = file.GetProperty("id").GetString()!;

        var other = CreateClient("ceiling-del-other");
        var response = await other.DeleteAsync($"/api/files/{fileId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_file_cannot_be_revived_by_extending_it()
    {
        // FR-030. Expiry is not a soft state that a late request can undo.
        //
        // The status is 403 rather than 404, and that is deliberate rather than an oversight.
        // Owner-scoped routes resolve ownership in the authorization handler, which treats
        // "expired", "never existed", and "not yours" identically — so all three deny the same
        // way. The assertion below is written against that property rather than against a status
        // code, because indistinguishability is the thing worth protecting: a caller must not be
        // able to learn that a file once existed by comparing responses.
        var client = CreateClient("ceiling-revive");
        var file = await UploadAsync(client);
        var fileId = file.GetProperty("id").GetString()!;

        var before = _factory.Clock.UtcNow;
        _factory.Clock.Advance(TimeSpan.FromHours(25));

        try
        {
            var expired = await ExtendAsync(client, fileId, _factory.Clock.UtcNow.AddDays(1));

            var neverExisted = await ExtendAsync(
                client,
                "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                _factory.Clock.UtcNow.AddDays(1));

            Assert.False(expired.IsSuccessStatusCode);
            Assert.Equal(neverExisted.StatusCode, expired.StatusCode);
        }
        finally
        {
            // The fixture is shared, so the clock is put back even if the assertion fails.
            _factory.Clock.AdvanceTo(before);
        }
    }

    [Fact]
    public async Task The_repository_refuses_a_ceiling_violation_even_without_the_endpoint()
    {
        // The endpoint is one way in. This is the check that makes the ceiling a property of the
        // data rather than of a handler — a future code path that forgets to validate still
        // cannot write an out-of-range expiry.
        var client = CreateClient("ceiling-datalayer");
        var file = await UploadAsync(client);

        var fileId = file.GetProperty("id").GetString()!;
        var uploadedAt = file.GetProperty("uploadedAt").GetDateTimeOffset();

        var files = _factory.Files;

        await Assert.ThrowsAsync<RetentionViolationException>(() =>
            files.UpdateExpiryAsync(fileId, uploadedAt.AddDays(31)));
    }

    [Fact]
    public async Task A_retention_change_is_audited_with_both_expiries()
    {
        // FR-046. One expiry alone does not say what changed, and "what changed" is the entire
        // reason to audit a retention edit.
        var client = CreateClient("ceiling-audit");
        var file = await UploadAsync(client);

        var fileId = file.GetProperty("id").GetString()!;
        var previous = file.GetProperty("expiresAt").GetDateTimeOffset();
        var target = file.GetProperty("uploadedAt").GetDateTimeOffset().AddDays(5);

        await ExtendAsync(client, fileId, target);

        var entry = Assert.Single(
            _factory.Audit.Entries,
            e => e.Action == Core.Models.AuditAction.RetentionExtend
                && e.TargetId == fileId
                && e.Outcome == Core.Models.AuditOutcome.Success);

        Assert.Equal(previous, entry.PreviousExpiresAt);
        Assert.Equal(target, entry.NewExpiresAt);
    }

    [Fact]
    public async Task A_refused_extension_is_audited_as_a_denial()
    {
        var client = CreateClient("ceiling-audit-denied");
        var file = await UploadAsync(client);

        var fileId = file.GetProperty("id").GetString()!;
        var uploadedAt = file.GetProperty("uploadedAt").GetDateTimeOffset();

        await ExtendAsync(client, fileId, uploadedAt.AddDays(60));

        Assert.Contains(_factory.Audit.Entries, e =>
            e.Action == Core.Models.AuditAction.RetentionExtend
            && e.TargetId == fileId
            && e.Outcome == Core.Models.AuditOutcome.Denied);
    }
}
