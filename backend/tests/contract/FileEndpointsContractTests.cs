using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BlinkMark.TestHost;

namespace BlinkMark.ContractTests;

/// <summary>
/// Conformance of the file endpoints to <c>contracts/openapi.yaml</c> (T030).
/// </summary>
/// <remarks>
/// Principle VI puts contracts before code, which only means something if something checks. These
/// tests assert the three things a consumer actually depends on: that every documented operation
/// exists, that it returns the documented status codes, and that the response body carries the
/// documented fields.
/// </remarks>
public sealed class FileEndpointsContractTests(BlinkMarkApiFactory factory)
    : IClassFixture<BlinkMarkApiFactory>
{
    private readonly BlinkMarkApiFactory _factory = factory;

    private HttpClient CreateAuthenticatedClient(string userId = "user-1", string name = "Ada Lovelace")
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

    [Fact]
    public async Task Post_files_returns_201_with_the_documented_body()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/files", FileContent("notes.md", "# Heading\n\nBody text."));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(body);

        foreach (var field in new[]
        {
            "id", "displayName", "contentType", "sizeBytes", "ownerId", "ownerDisplayName",
            "uploadedAt", "expiresAt", "maxExpiresAt", "previewUrl", "accessScopeNotice",
            "retentionNotice", "isOwner",
        })
        {
            Assert.True(body!.ContainsKey(field), $"The documented field '{field}' is missing from the response.");
        }
    }

    [Fact]
    public async Task Post_files_returns_400_when_the_file_is_not_a_supported_type()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/files", FileContent("payload.exe", "MZ binary"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_files_returns_the_documented_list_shape()
    {
        var client = CreateAuthenticatedClient("user-list", "Grace Hopper");
        await client.PostAsync("/api/files", FileContent("draft.md", "Some prose."));

        var response = await client.GetAsync("/api/files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        Assert.NotNull(body);
        Assert.True(body!.ContainsKey("files"));
        Assert.True(body.ContainsKey("quota"));

        var quota = body["quota"];
        Assert.True(quota.TryGetProperty("liveFiles", out _));
        Assert.True(quota.TryGetProperty("maxLiveFiles", out _));
    }

    [Fact]
    public async Task Get_file_returns_404_for_an_unknown_id()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/files/{Core.Models.Identifiers.New()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_file_content_returns_the_text_projection()
    {
        var client = CreateAuthenticatedClient("user-content", "Alan Turing");

        var created = await client.PostAsync("/api/files", FileContent("paper.md", "# On computable numbers"));
        var detail = await created.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var fileId = detail!["id"].GetString()!;

        var response = await client.GetAsync($"/api/files/{fileId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        Assert.Contains("On computable numbers", body!["text"].GetString());
        Assert.False(string.IsNullOrWhiteSpace(body["renderVersion"].GetString()));
    }

    [Fact]
    public async Task Delete_file_returns_204_for_the_owner_and_404_afterwards()
    {
        var client = CreateAuthenticatedClient("user-delete", "Katherine Johnson");

        var created = await client.PostAsync("/api/files", FileContent("temp.md", "Delete me."));
        var detail = await created.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var fileId = detail!["id"].GetString()!;

        var deleted = await client.DeleteAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var afterwards = await client.GetAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.NotFound, afterwards.StatusCode);
    }

    [Fact]
    public async Task Every_response_carries_a_correlation_id()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/files");

        Assert.True(
            response.Headers.Contains("X-Correlation-Id"),
            "Principle V requires the correlation id to be observable by the caller.");
    }
}
