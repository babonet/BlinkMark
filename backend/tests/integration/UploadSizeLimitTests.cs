using System.Net;
using System.Net.Http.Headers;
using BlinkMark.Infrastructure.Configuration;
using BlinkMark.TestHost;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BlinkMark.IntegrationTests;

/// <summary>
/// The upload size limit, and the transport limits underneath it.
/// </summary>
/// <remarks>
/// <para>
/// There are three size limits in play, and only one of them is supposed to do the refusing.
/// <c>UploadValidator</c> refuses with a message naming the limit; Kestrel and the multipart form
/// reader refuse by aborting the request, which reaches the uploader as a bare 413 with nothing
/// to act on.
/// </para>
/// <para>
/// While the upload limit was 10 MB and the framework defaults were 30 MB and 128 MB, that
/// ordering held by luck rather than by construction. These tests pin it, because the failure
/// when it stops holding is silent from the application's point of view: no exception, no log,
/// just a worse error message for anyone uploading a large file.
/// </para>
/// </remarks>
public sealed class UploadSizeLimitTests(BlinkMarkApiFactory factory) : IClassFixture<BlinkMarkApiFactory>
{
    private readonly BlinkMarkApiFactory _factory = factory;

    [Fact]
    public void TheMultipartLimitLeavesRoomForTheUploadLimit()
    {
        var upload = _factory.Services.GetRequiredService<IOptions<BlinkMarkOptions>>().Value.Upload;
        var form = _factory.Services.GetRequiredService<IOptions<FormOptions>>().Value;

        // Strictly greater: a file exactly at the limit is allowed, and it still has to carry
        // multipart framing on top of its own bytes.
        Assert.True(
            form.MultipartBodyLengthLimit > upload.MaxSizeBytes,
            $"The multipart body limit ({form.MultipartBodyLengthLimit} bytes) must exceed the upload limit "
                + $"({upload.MaxSizeBytes} bytes), or the form reader aborts the request before UploadValidator "
                + "can explain why it was refused.");
    }

    [Fact]
    public async Task AFileOverTheLimitIsRefusedWithAnExplanation()
    {
        var upload = _factory.Services.GetRequiredService<IOptions<BlinkMarkOptions>>().Value.Upload;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Tokens.IssueUserToken("alice", "Alice"));

        // Just over, not wildly over. A file far beyond the limit could be refused by a transport
        // limit and still look like a pass here; one byte over can only be refused by the
        // validator.
        var oversized = new string('a', (int)upload.MaxSizeBytes + 1);

        using var form = new MultipartFormDataContent();
        var content = new StringContent(oversized);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(content, "file", "oversized.md");

        var response = await client.PostAsync("/api/files", form);

        // 400, which is what the upload endpoint returns for every validation refusal. Worth
        // noting that 413 would say more for this particular case — but the status is a contract
        // and changing it is a separate decision from raising the limit.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The message has to name the limit. "Too large" without a number tells someone their
        // upload failed but not what would succeed.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"{upload.MaxSizeBytes / (1024 * 1024)} MB", body);
    }

    [Fact]
    public async Task AFileAtTheLimitIsAccepted()
    {
        var upload = _factory.Services.GetRequiredService<IOptions<BlinkMarkOptions>>().Value.Upload;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Tokens.IssueUserToken("bob", "Bob"));

        // Exactly at the limit. This is the case the transport limits would break first, and the
        // one an off-by-one in the headroom calculation would catch.
        var atLimit = new string('a', (int)upload.MaxSizeBytes);

        using var form = new MultipartFormDataContent();
        var content = new StringContent(atLimit);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(content, "file", "at-limit.md");

        var response = await client.PostAsync("/api/files", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
