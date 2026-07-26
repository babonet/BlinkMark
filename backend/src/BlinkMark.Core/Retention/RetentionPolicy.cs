using BlinkMark.Core.Abstractions;

namespace BlinkMark.Core.Retention;

/// <summary>
/// The retention rules, in one place (T015).
/// </summary>
/// <remarks>
/// Constitution Principle II: 24 hours by default, 30 days absolutely, platform-enforced
/// deletion, and an expired file reads as deleted.
/// <para>
/// The important design decision here is that the ceiling is <em>data</em>. <c>maxExpiresAt</c>
/// is computed once at upload and stored on the file. Every later write of <c>expiresAt</c> is
/// checked against that stored value rather than against a freshly recomputed one. The
/// difference matters: a recomputed ceiling can be got wrong by a new code path, and a
/// recomputed ceiling based on "now" instead of "uploadedAt" would let a file be extended
/// indefinitely, 30 days at a time. Storing it removes both possibilities (FR-028).
/// </para>
/// </remarks>
public static class RetentionPolicy
{
    /// <summary>FR-026. What an uploader gets without asking for anything.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);

    /// <summary>FR-028. The ceiling, measured from upload and never from the extension request.</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(30);

    /// <summary>An extension must move the expiry at least this far to be worth doing.</summary>
    public static readonly TimeSpan MinimumExtension = TimeSpan.FromMinutes(1);

    /// <summary>The default expiry for a file uploaded at <paramref name="uploadedAt"/>.</summary>
    public static DateTimeOffset DefaultExpiresAt(DateTimeOffset uploadedAt) =>
        uploadedAt + DefaultLifetime;

    /// <summary>
    /// The ceiling for a file uploaded at <paramref name="uploadedAt"/>. Computed once, at
    /// upload, and then stored.
    /// </summary>
    public static DateTimeOffset MaxExpiresAt(DateTimeOffset uploadedAt) =>
        uploadedAt + MaximumLifetime;

    /// <summary>
    /// Validates a proposed expiry against the stored ceiling and the current time.
    /// </summary>
    /// <remarks>
    /// Called by the repository on every write of <c>expiresAt</c>, not only by the retention
    /// endpoint. A rule enforced at the entry point is a rule that a second entry point can
    /// skip.
    /// </remarks>
    public static RetentionValidationResult Validate(
        DateTimeOffset proposedExpiresAt,
        DateTimeOffset maxExpiresAt,
        DateTimeOffset now)
    {
        if (proposedExpiresAt <= now)
        {
            return RetentionValidationResult.Invalid(
                RetentionViolation.InThePast,
                "Expiry must be in the future.");
        }

        if (proposedExpiresAt > maxExpiresAt)
        {
            return RetentionValidationResult.Invalid(
                RetentionViolation.ExceedsCeiling,
                $"A file can never outlive 30 days from upload. The latest possible expiry for this file is {maxExpiresAt:u}.");
        }

        return RetentionValidationResult.Valid;
    }

    /// <summary>
    /// The Cosmos per-item TTL, in seconds, that corresponds to <paramref name="expiresAt"/>.
    /// </summary>
    /// <remarks>
    /// Minimum of one second: Cosmos rejects a TTL of zero or less, and a file that is already
    /// expired should be swept immediately rather than refused.
    /// </remarks>
    public static int TtlSeconds(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var remaining = expiresAt - now;
        return remaining <= TimeSpan.Zero ? 1 : (int)Math.Ceiling(remaining.TotalSeconds);
    }

    /// <summary>Human-readable notice shown at upload (FR-075, SC-016).</summary>
    public static string RetentionNotice(DateTimeOffset expiresAt) =>
        $"This file is deleted automatically at {expiresAt:u} and can never be kept longer than 30 days from upload. " +
        "There is no backup — download a copy if you need one after that.";
}

/// <summary>Why a proposed expiry was refused.</summary>
public enum RetentionViolation
{
    None,
    InThePast,
    ExceedsCeiling,
}

/// <summary>The outcome of validating a proposed expiry.</summary>
public sealed record RetentionValidationResult
{
    public static readonly RetentionValidationResult Valid = new()
    {
        IsValid = true,
        Violation = RetentionViolation.None,
        Message = null,
    };

    public required bool IsValid { get; init; }

    public required RetentionViolation Violation { get; init; }

    public required string? Message { get; init; }

    public static RetentionValidationResult Invalid(RetentionViolation violation, string message) => new()
    {
        IsValid = false,
        Violation = violation,
        Message = message,
    };
}

/// <summary>
/// Thrown when a write would breach the retention rules.
/// </summary>
/// <remarks>
/// Raised at the data layer rather than returned, so that a caller who forgets to check a result
/// still cannot commit the write.
/// </remarks>
public sealed class RetentionViolationException(RetentionViolation violation, string message)
    : InvalidOperationException(message)
{
    public RetentionViolation Violation { get; } = violation;
}

/// <summary>Convenience wrapper binding <see cref="RetentionPolicy"/> to a clock.</summary>
public sealed class RetentionRules(IClock clock)
{
    private readonly IClock _clock = clock;

    public DateTimeOffset DefaultExpiresAt(DateTimeOffset uploadedAt) =>
        RetentionPolicy.DefaultExpiresAt(uploadedAt);

    public DateTimeOffset MaxExpiresAt(DateTimeOffset uploadedAt) =>
        RetentionPolicy.MaxExpiresAt(uploadedAt);

    public int TtlSeconds(DateTimeOffset expiresAt) =>
        RetentionPolicy.TtlSeconds(expiresAt, _clock.UtcNow);

    /// <summary>Validates, and throws if the proposed expiry is not allowed.</summary>
    public void EnsureValid(DateTimeOffset proposedExpiresAt, DateTimeOffset maxExpiresAt)
    {
        var result = RetentionPolicy.Validate(proposedExpiresAt, maxExpiresAt, _clock.UtcNow);
        if (!result.IsValid)
        {
            throw new RetentionViolationException(result.Violation, result.Message!);
        }
    }
}
