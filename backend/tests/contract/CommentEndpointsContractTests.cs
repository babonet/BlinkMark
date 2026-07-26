using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlinkMark.TestHost;

namespace BlinkMark.ContractTests;

/// <summary>
/// Conformance of the comment endpoints to <c>contracts/openapi.yaml</c> (T051).
/// </summary>
public sealed class CommentEndpointsContractTests(BlinkMarkApiFactory factory)
    : IClassFixture<BlinkMarkApiFactory>
{
    private readonly BlinkMarkApiFactory _factory = factory;

    private HttpClient CreateClient(string userId, string name = "Reviewer")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Tokens.IssueUserToken(userId, name));
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

    private static object Anchor(string exact, int start = 0, string prefix = "", string suffix = "") => new
    {
        kind = "text",
        exact,
        prefix,
        suffix,
        start,
        end = start + exact.Length,
    };

    private async Task<string> CreateFileAsync(HttpClient client, string markdown = "# Heading\n\nThe ingest path is the bottleneck.")
    {
        var created = await client.PostAsync("/api/files", FileContent("draft.md", markdown));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Post_comment_returns_201_with_the_documented_body()
    {
        var client = CreateClient("comment-author", "Ada Lovelace");
        var fileId = await CreateFileAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new { body = "This is the bottleneck we discussed.", anchor = Anchor("ingest path") });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var comment = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var field in new[]
        {
            "id", "fileId", "threadId", "parentId", "body", "authorId", "authorDisplayName",
            "actingAgentId", "createdAt", "editedAt", "anchor", "anchorState", "deletedAt",
        })
        {
            Assert.True(comment.TryGetProperty(field, out _), $"Documented field '{field}' is missing.");
        }

        // A root comment is its own thread (FR-021).
        Assert.Equal(comment.GetProperty("id").GetString(), comment.GetProperty("threadId").GetString());
        Assert.Equal(JsonValueKind.Null, comment.GetProperty("parentId").ValueKind);
    }

    [Fact]
    public async Task The_author_is_taken_from_the_token_not_the_request()
    {
        var client = CreateClient("real-author", "Real Author");
        var fileId = await CreateFileAsync(client);

        // A client that tries to attribute its comment to somebody else.
        var response = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new
            {
                body = "Attributed to someone else, supposedly.",
                anchor = Anchor("ingest path"),
                authorId = "someone-else",
                authorDisplayName = "Someone Else",
            });

        var comment = await response.Content.ReadFromJsonAsync<JsonElement>();

        // FR-005, FR-023. The extra fields are not merely rejected, they are inert.
        Assert.Equal("real-author", comment.GetProperty("authorId").GetString());
        Assert.Equal("Real Author", comment.GetProperty("authorDisplayName").GetString());
    }

    [Fact]
    public async Task Get_comments_returns_an_array()
    {
        var client = CreateClient("comment-lister");
        var fileId = await CreateFileAsync(client);

        await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new { body = "First.", anchor = Anchor("ingest path") });

        var response = await client.GetAsync($"/api/files/{fileId}/comments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var comments = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, comments.ValueKind);
        Assert.Single(comments.EnumerateArray());
    }

    [Fact]
    public async Task A_reply_inherits_its_roots_thread()
    {
        var client = CreateClient("thread-starter");
        var fileId = await CreateFileAsync(client);

        var rootResponse = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new { body = "Root comment.", anchor = Anchor("ingest path") });
        var root = await rootResponse.Content.ReadFromJsonAsync<JsonElement>();
        var rootId = root.GetProperty("id").GetString()!;

        var replyResponse = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new { body = "Reply.", anchor = Anchor("ingest path"), parentId = rootId });

        Assert.Equal(HttpStatusCode.Created, replyResponse.StatusCode);

        var reply = await replyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(rootId, reply.GetProperty("threadId").GetString());
        Assert.Equal(rootId, reply.GetProperty("parentId").GetString());
    }

    [Fact]
    public async Task Patch_comment_returns_200_and_stamps_an_edit_time()
    {
        var client = CreateClient("editor");
        var fileId = await CreateFileAsync(client);

        var created = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new { body = "Original.", anchor = Anchor("ingest path") });
        var commentId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await client.PatchAsJsonAsync(
            $"/api/files/{fileId}/comments/{commentId}",
            new { body = "Revised." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var edited = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Revised.", edited.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
    }

    [Fact]
    public async Task Editing_someone_elses_comment_is_forbidden()
    {
        var author = CreateClient("author-1", "Author One");
        var fileId = await CreateFileAsync(author);

        var created = await author.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new { body = "Mine.", anchor = Anchor("ingest path") });
        var commentId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var interloper = CreateClient("interloper", "Interloper");

        var edit = await interloper.PatchAsJsonAsync(
            $"/api/files/{fileId}/comments/{commentId}",
            new { body = "Not mine to change." });

        // Not even the file owner may rewrite somebody's words.
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);

        var delete = await interloper.DeleteAsync($"/api/files/{fileId}/comments/{commentId}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task Delete_comment_returns_204_and_the_body_stops_being_readable()
    {
        var client = CreateClient("deleter");
        var fileId = await CreateFileAsync(client);

        var created = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new { body = "Regrettable remark.", anchor = Anchor("ingest path") });
        var commentId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await client.DeleteAsync($"/api/files/{fileId}/comments/{commentId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var listed = await (await client.GetAsync($"/api/files/{fileId}/comments"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var deleted = listed.EnumerateArray().Single();

        // Soft delete keeps the row so replies survive, but the text must genuinely go. A
        // "deleted" comment whose body is still readable has not been deleted.
        Assert.NotEqual(JsonValueKind.Null, deleted.GetProperty("deletedAt").ValueKind);
        Assert.Equal(string.Empty, deleted.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Comments_on_an_unknown_or_expired_file_return_404()
    {
        var client = CreateClient("ghost-commenter");

        var unknown = await client.GetAsync($"/api/files/{Core.Models.Identifiers.New()}/comments");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var fileId = await CreateFileAsync(client);
        _factory.Clock.Advance(TimeSpan.FromHours(25));

        var expired = await client.GetAsync($"/api/files/{fileId}/comments");
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);

        var writeToExpired = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new { body = "Too late.", anchor = Anchor("ingest path") });
        Assert.Equal(HttpStatusCode.NotFound, writeToExpired.StatusCode);
    }

    [Fact]
    public async Task An_empty_or_unanchored_comment_is_refused()
    {
        var client = CreateClient("bad-input");
        var fileId = await CreateFileAsync(client);

        var empty = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new { body = "   ", anchor = Anchor("ingest path") });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var unanchored = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/comments",
            new { body = "Anchored to nothing.", anchor = Anchor(string.Empty) });
        Assert.Equal(HttpStatusCode.BadRequest, unanchored.StatusCode);
    }
}
