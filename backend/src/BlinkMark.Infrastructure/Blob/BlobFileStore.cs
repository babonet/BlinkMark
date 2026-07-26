using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using BlinkMark.Core.Abstractions;
using BlinkMark.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlinkMark.Infrastructure.Blob;

/// <summary>
/// File content in blob storage, with platform-enforced expiry (T017).
/// </summary>
/// <remarks>
/// The expiry is the point of this class. Every artifact is written with an <em>absolute</em>
/// deletion time set on the blob itself, so the platform performs the deletion whether or not
/// any BlinkMark process is running, healthy, or deployed. Principle II asks for exactly that: a
/// cleanup job that has to succeed for content to disappear is a job that can fail silently, and
/// SC-006 would then be untrue without anyone noticing.
/// <para>
/// Set Blob Expiry is only available on hierarchical-namespace accounts, and it is surfaced
/// through the Data Lake client rather than the blob client — hence the two clients below. The
/// blob client handles content; the Data Lake client handles scheduling.
/// </para>
/// </remarks>
public sealed class BlobFileStore : IBlobFileStore
{
    private readonly BlobServiceClient _blobService;
    private readonly DataLakeServiceClient _dataLakeService;
    private readonly BlobOptions _options;
    private readonly ILogger<BlobFileStore> _logger;

    public BlobFileStore(
        BlobServiceClient blobService,
        DataLakeServiceClient dataLakeService,
        IOptions<BlinkMarkOptions> options,
        ILogger<BlobFileStore> logger)
    {
        _blobService = blobService;
        _dataLakeService = dataLakeService;
        _options = options.Value.Blob;
        _logger = logger;
    }

    public async Task WriteAsync(
        string fileId,
        BlobArtifact artifact,
        Stream content,
        string contentType,
        DateTimeOffset expiresAt,
        string? renderVersion = null,
        CancellationToken cancellationToken = default)
    {
        var containerName = ContainerFor(artifact);
        var blobName = BlobNameFor(fileId, artifact, renderVersion);

        var container = _blobService.GetBlobContainerClient(containerName);
        var blob = container.GetBlobClient(blobName);

        await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = contentType,
                    // Belt and braces against content sniffing. The preview response sets this
                    // header too; setting it on the object means it survives any future path
                    // that streams the blob directly.
                    ContentDisposition = "inline",
                    CacheControl = "private, no-store",
                },
            },
            cancellationToken).ConfigureAwait(false);

        await ScheduleDeletionAsync(containerName, blobName, expiresAt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream?> ReadAsync(
        string fileId,
        BlobArtifact artifact,
        string? renderVersion = null,
        CancellationToken cancellationToken = default)
    {
        var blob = _blobService
            .GetBlobContainerClient(ContainerFor(artifact))
            .GetBlobClient(BlobNameFor(fileId, artifact, renderVersion));

        try
        {
            var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Value.Content;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // Deleted, expired, or never existed. The caller cannot tell the difference, and
            // must not be able to (FR-032).
            return null;
        }
    }

    public async Task<string?> ReadTextAsync(
        string fileId,
        BlobArtifact artifact,
        string? renderVersion = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await ReadAsync(fileId, artifact, renderVersion, cancellationToken)
            .ConfigureAwait(false);

        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateExpiryAsync(
        string fileId,
        string renderVersion,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        foreach (var (container, blobName) in EnumerateArtifacts(fileId, renderVersion))
        {
            await ScheduleDeletionAsync(container, blobName, expiresAt, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteAllAsync(
        string fileId,
        string renderVersion,
        CancellationToken cancellationToken = default)
    {
        foreach (var (containerName, blobName) in EnumerateArtifacts(fileId, renderVersion))
        {
            var blob = _blobService.GetBlobContainerClient(containerName).GetBlobClient(blobName);

            // IncludeSnapshots, not just the base blob. A surviving snapshot would mean the file
            // is still physically present after the API reports it deleted, which is precisely
            // the failure SC-006 has to rule out.
            await blob.DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> ExistsAsync(
        string fileId,
        BlobArtifact artifact,
        string? renderVersion = null,
        CancellationToken cancellationToken = default)
    {
        var blob = _blobService
            .GetBlobContainerClient(ContainerFor(artifact))
            .GetBlobClient(BlobNameFor(fileId, artifact, renderVersion));

        var response = await blob.ExistsAsync(cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    private async Task ScheduleDeletionAsync(
        string containerName,
        string blobName,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var file = _dataLakeService
            .GetFileSystemClient(containerName)
            .GetFileClient(blobName);

        try
        {
            // Absolute mode. Relative-to-creation would silently ignore a retention extension,
            // because the creation time never moves.
            await file.ScheduleDeletionAsync(
                new DataLakeFileScheduleDeletionOptions(expiresAt),
                cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            _logger.LogWarning(
                "Could not schedule deletion for {Container}/{Blob}: the blob no longer exists.",
                containerName,
                blobName);
        }
    }

    private IEnumerable<(string Container, string BlobName)> EnumerateArtifacts(string fileId, string renderVersion)
    {
        yield return (_options.OriginalsContainer, BlobNameFor(fileId, BlobArtifact.Original, null));
        yield return (_options.RendersContainer, BlobNameFor(fileId, BlobArtifact.Render, renderVersion));
        yield return (_options.ProjectionsContainer, BlobNameFor(fileId, BlobArtifact.Projection, renderVersion));
    }

    private string ContainerFor(BlobArtifact artifact) => artifact switch
    {
        BlobArtifact.Original => _options.OriginalsContainer,
        BlobArtifact.Render => _options.RendersContainer,
        BlobArtifact.Projection => _options.ProjectionsContainer,
        _ => throw new ArgumentOutOfRangeException(nameof(artifact)),
    };

    /// <summary>
    /// Builds the object name. Never derived from the uploaded filename (FR-008).
    /// </summary>
    private static string BlobNameFor(string fileId, BlobArtifact artifact, string? renderVersion)
    {
        if (!Core.Models.Identifiers.IsValid(fileId))
        {
            throw new ArgumentException("File id must be a valid identifier.", nameof(fileId));
        }

        return artifact switch
        {
            BlobArtifact.Original => fileId,
            BlobArtifact.Render => $"{fileId}/{RequireRenderVersion(renderVersion)}.html",
            BlobArtifact.Projection => $"{fileId}/{RequireRenderVersion(renderVersion)}.txt",
            _ => throw new ArgumentOutOfRangeException(nameof(artifact)),
        };
    }

    private static string RequireRenderVersion(string? renderVersion)
    {
        if (string.IsNullOrWhiteSpace(renderVersion))
        {
            throw new ArgumentException("A render version is required for render and projection artifacts.");
        }

        // The render version becomes part of a path, so it is constrained rather than trusted.
        foreach (var character in renderVersion)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                throw new ArgumentException($"Render version '{renderVersion}' contains an unsupported character.");
            }
        }

        return renderVersion;
    }
}
