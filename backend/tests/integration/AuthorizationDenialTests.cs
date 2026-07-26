using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BlinkMark.TestHost;

namespace BlinkMark.IntegrationTests;

/// <summary>
/// Authorization denial paths (T031). Mandatory under Principle VI.
/// </summary>
/// <remarks>
/// Principle I is the one principle whose failure is unrecoverable — a leaked draft cannot be
/// un-leaked — so these tests probe the three ways it could break in practice: no credential,
/// a credential from the wrong organization, and a credential that has expired.
/// <para>
/// The cross-tenant case is the important one, and it is a real test rather than a mock. The
/// token is genuinely signed and structurally valid; only its issuer is wrong. It is refused
/// because the production token handler pins the issuer to a single tenant, so a regression that
/// loosened that pinning would fail here.
/// </para>
/// </remarks>
public sealed class AuthorizationDenialTests(BlinkMarkApiFactory factory)
    : IClassFixture<BlinkMarkApiFactory>
{
    private readonly BlinkMarkApiFactory _factory = factory;

    private static MultipartFormDataContent FileContent(string fileName, string content)
    {
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(bytes, "file", fileName);
        return form;
    }

    [Theory]
    [InlineData("GET", "/api/files")]
    [InlineData("POST", "/api/files")]
    public async Task Unauthenticated_requests_are_refused(string method, string path)
    {
        var client = _factory.CreateClient();

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = FileContent("notes.md", "text");
        }

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_reveal_nothing_about_whether_a_file_exists()
    {
        var owner = _factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Tokens.IssueUserToken("owner-secrecy", "Owner"));

        var created = await owner.PostAsync("/api/files", FileContent("secret.md", "Confidential."));
        var detail = await created.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var realFileId = detail!["id"].GetString()!;

        var anonymous = _factory.CreateClient();

        var existing = await anonymous.GetAsync($"/api/files/{realFileId}");
        var madeUp = await anonymous.GetAsync($"/api/files/{Core.Models.Identifiers.New()}");

        // Identical responses. If a real file produced 401 and an imaginary one produced 404, an
        // unauthenticated caller could enumerate which identifiers exist (FR-001).
        Assert.Equal(existing.StatusCode, madeUp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, existing.StatusCode);
        Assert.Equal(0, existing.Content.Headers.ContentLength ?? 0);
    }

    [Fact]
    public async Task A_token_from_another_tenant_is_refused()
    {
        var otherTenant = new TestTokenIssuer(BlinkMarkApiFactory.OtherTenantId, BlinkMarkApiFactory.ApiAudience);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", otherTenant.IssueUserToken("outsider", "Outsider"));

        var response = await client.GetAsync("/api/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.Tokens.IssueUserToken(
                "expired-user",
                "Expired",
                expiresAt: DateTimeOffset.UtcNow.AddHours(-2)));

        var response = await client.GetAsync("/api/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_minted_for_a_different_audience_is_refused()
    {
        var wrongAudience = new TestTokenIssuer(BlinkMarkApiFactory.TenantId, "api://some-other-service");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", wrongAudience.IssueUserToken("user-1", "Ada Lovelace"));

        var response = await client.GetAsync("/api/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_app_only_token_is_refused_because_nobody_is_signed_in_behind_it()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Tokens.IssueAppOnlyToken(BlinkMarkApiFactory.ApprovedAgentClientId));

        var response = await client.GetAsync("/api/files");

        // FR-055: no standing agent identity, and nothing acts for a signed-out user. Enforcing
        // it at the token layer means it holds for every endpoint, including ones not written
        // yet — an application permission granted by mistake still cannot reach anything.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_obtained_by_an_unapproved_client_is_refused()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.Tokens.IssueUserToken(
                "user-1",
                "Ada Lovelace",
                authorizedPartyOverride: "some-unrelated-internal-app"));

        var response = await client.GetAsync("/api/files");

        // Any application in the tenant can request a token for a resource its users consent to.
        // Without the authorized-party check, an unrelated internal app could read BlinkMark
        // drafts simply by asking its own users to sign in.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_carrying_none_of_this_apis_scopes_is_refused()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.Tokens.IssueUserToken(
                "user-1",
                "Ada Lovelace",
                scopesOverride: "User.Read"));

        var response = await client.GetAsync("/api/files");

        // A valid token for the right tenant and the right audience is still not consent to use
        // this API. The scope is what the user actually agreed to.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_agent_token_is_accepted_and_recorded_as_agent_initiated()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.Tokens.IssueAgentToken(
                "user-agent-audit",
                "Ada Lovelace",
                BlinkMarkApiFactory.ApprovedAgentClientId));

        var created = await client.PostAsync("/api/files", FileContent("agent.md", "Written by an agent."));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var detail = await created.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var fileId = detail!["id"].GetString()!;

        // FR-049 and SC-015: the action is permitted, and it is distinguishable from one the
        // person performed themselves.
        var entry = Assert.Single(_factory.Audit.ForTarget(fileId));
        Assert.Equal("user-agent-audit", entry.ActorId);
        Assert.Equal(BlinkMarkApiFactory.ApprovedAgentClientId, entry.ActingAgentId);
    }

    [Fact]
    public async Task A_non_owner_in_the_tenant_can_read_but_cannot_delete()
    {
        var owner = _factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Tokens.IssueUserToken("owner-2", "Owner Two"));

        var created = await owner.PostAsync("/api/files", FileContent("shared.md", "Shared draft."));
        var detail = await created.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var fileId = detail!["id"].GetString()!;

        var colleague = _factory.CreateClient();
        colleague.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Tokens.IssueUserToken("colleague-2", "Colleague Two"));

        // Clarification Q1: any authenticated organization member holding the link may view.
        var read = await colleague.GetAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // But owner-only actions stay owner-only (FR-029, FR-058).
        var delete = await colleague.DeleteAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task An_expired_file_is_refused_to_everyone_including_its_owner()
    {
        var owner = _factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Tokens.IssueUserToken("owner-3", "Owner Three"));

        var created = await owner.PostAsync("/api/files", FileContent("ephemeral.md", "Short lived."));
        var detail = await created.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        var fileId = detail!["id"].GetString()!;

        _factory.Clock.Advance(TimeSpan.FromHours(25));

        var response = await owner.GetAsync($"/api/files/{fileId}");

        // Ownership does not survive expiry. Principle II outranks convenience here.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
