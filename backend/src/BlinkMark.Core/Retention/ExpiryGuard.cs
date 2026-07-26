using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;

namespace BlinkMark.Core.Retention;

/// <summary>
/// The expired-reads-as-deleted guard (T042).
/// </summary>
/// <remarks>
/// Applied on every read path, without exception.
/// <para>
/// Two mechanisms already remove expired content: blob expiry and Cosmos TTL. Neither is
/// instantaneous. There is a window — seconds, sometimes minutes — between the moment a file
/// expires and the moment the platform physically removes it, and during that window the bytes
/// are still there and the document is still readable.
/// </para>
/// <para>
/// FR-032 says content that has expired but has not yet been purged must read as deleted. This
/// guard is what makes that true, and it is why it belongs at the read path rather than in a
/// sweep: a user must never see a file after its stated expiry, whatever the cleanup schedule
/// happens to be doing.
/// </para>
/// <para>
/// Not-found and expired return the same answer deliberately. Distinguishing them would tell a
/// caller that a file used to exist here, which is information they are not entitled to.
/// </para>
/// </remarks>
public sealed class ExpiryGuard(IClock clock)
{
    private readonly IClock _clock = clock;

    /// <summary>
    /// Returns the file if it is readable, and <see langword="null"/> if it is missing or expired.
    /// </summary>
    public FileRecord? Filter(FileRecord? file) =>
        file is null || file.IsExpired(_clock.UtcNow) ? null : file;

    /// <summary>Removes expired files from a list.</summary>
    public IReadOnlyList<FileRecord> Filter(IEnumerable<FileRecord> files)
    {
        var now = _clock.UtcNow;
        return files.Where(file => !file.IsExpired(now)).ToList();
    }

    /// <summary>Returns the file, or throws the same exception a missing file would.</summary>
    public FileRecord Require(FileRecord? file) =>
        Filter(file) ?? throw new FileNotFoundException("The file was not found.");

    /// <summary>How long is left, floored at zero.</summary>
    public TimeSpan RemainingLifetime(FileRecord file)
    {
        var remaining = file.ExpiresAt - _clock.UtcNow;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
