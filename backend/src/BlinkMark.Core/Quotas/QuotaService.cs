using BlinkMark.Core.Abstractions;

namespace BlinkMark.Core.Quotas;

/// <summary>The result of a quota or rate-limit check.</summary>
public sealed record QuotaCheckResult
{
    public required bool IsAllowed { get; init; }

    public string? Message { get; init; }

    public int LiveFiles { get; init; }

    public int MaxLiveFiles { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public static QuotaCheckResult Allowed(int liveFiles, int maxLiveFiles) => new()
    {
        IsAllowed = true,
        LiveFiles = liveFiles,
        MaxLiveFiles = maxLiveFiles,
    };

    public static QuotaCheckResult Denied(string message, int liveFiles, int maxLiveFiles, TimeSpan? retryAfter = null) => new()
    {
        IsAllowed = false,
        Message = message,
        LiveFiles = liveFiles,
        MaxLiveFiles = maxLiveFiles,
        RetryAfter = retryAfter,
    };
}

/// <summary>
/// Live-file quota and upload rate limiting (T043).
/// </summary>
/// <remarks>
/// Redis holds the counters because they must be shared across API replicas — a limiter in
/// replica memory grants every replica the full limit, and the constitution's stateless
/// requirement forbids it anyway.
/// <para>
/// The quota is checked against Redis for speed and <strong>confirmed against Cosmos before the
/// upload commits</strong>, with Cosmos authoritative. That ordering avoids a specific failure:
/// if Redis were the authority, a cache flush would silently grant everyone unlimited uploads,
/// and nobody would notice until the storage bill arrived.
/// </para>
/// <para>
/// FR-088 — capacity frees itself as files expire — needs no code at all. The quota is a live
/// count, and expiry reduces it. That matters because clarification Q1 established there is no
/// administrator to unblock a user who hits the cap; if the limit did not release itself, the
/// product would have a dead end with nobody able to open it.
/// </para>
/// </remarks>
public sealed class QuotaService(
    IFileRepository files,
    IRateLimitStore rateLimits,
    IClock clock,
    QuotaSettings settings)
{
    private readonly IFileRepository _files = files;
    private readonly IRateLimitStore _rateLimits = rateLimits;
    private readonly IClock _clock = clock;
    private readonly QuotaSettings _settings = settings;

    /// <summary>Checks both the live-file cap and the upload rate limit.</summary>
    public async Task<QuotaCheckResult> CheckUploadAllowedAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var window = CurrentWindow(_settings.UploadWindow);
        var attempts = await _rateLimits
            .IncrementAsync($"ratelimit:user:{userId}:{window}", _settings.UploadWindow, cancellationToken)
            .ConfigureAwait(false);

        if (attempts > _settings.MaxUploadsPerWindow)
        {
            var liveNow = await _files.CountLiveByOwnerAsync(userId, cancellationToken).ConfigureAwait(false);

            return QuotaCheckResult.Denied(
                $"You have uploaded {_settings.MaxUploadsPerWindow} files in the last "
                + $"{FormatWindow(_settings.UploadWindow)}. Try again shortly.",
                liveNow,
                _settings.MaxLiveFilesPerUser,
                _settings.UploadWindow);
        }

        // Cosmos is the authority. The cache above only decides how often this runs.
        var liveFiles = await _files.CountLiveByOwnerAsync(userId, cancellationToken).ConfigureAwait(false);

        if (liveFiles >= _settings.MaxLiveFilesPerUser)
        {
            return QuotaCheckResult.Denied(
                $"You have {liveFiles} live files, which is the limit of {_settings.MaxLiveFilesPerUser}. "
                + "Delete one, or wait for one to expire — capacity frees itself as files reach their expiry.",
                liveFiles,
                _settings.MaxLiveFilesPerUser);
        }

        return QuotaCheckResult.Allowed(liveFiles, _settings.MaxLiveFilesPerUser);
    }

    /// <summary>The user's current quota position, for display.</summary>
    public async Task<QuotaCheckResult> GetStatusAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var liveFiles = await _files.CountLiveByOwnerAsync(userId, cancellationToken).ConfigureAwait(false);
        return QuotaCheckResult.Allowed(liveFiles, _settings.MaxLiveFilesPerUser);
    }

    private long CurrentWindow(TimeSpan window) =>
        _clock.UtcNow.ToUnixTimeSeconds() / (long)window.TotalSeconds;

    private static string FormatWindow(TimeSpan window) =>
        window.TotalHours >= 1
            ? $"{window.TotalHours:0.#} hour(s)"
            : $"{window.TotalMinutes:0.#} minute(s)";
}

/// <summary>Quota and rate-limit thresholds.</summary>
public sealed record QuotaSettings
{
    /// <summary>FR-083.</summary>
    public int MaxLiveFilesPerUser { get; init; } = 50;

    /// <summary>FR-085.</summary>
    public int MaxUploadsPerWindow { get; init; } = 20;

    public TimeSpan UploadWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>FR-054.</summary>
    public int MaxAgentRequestsPerWindow { get; init; } = 120;

    public TimeSpan AgentWindow { get; init; } = TimeSpan.FromMinutes(1);
}
