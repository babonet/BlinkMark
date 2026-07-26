using BlinkMark.Core.Abstractions;

namespace BlinkMark.TestHost;

/// <summary>
/// A clock the test controls.
/// </summary>
/// <remarks>
/// Retention is the product. Every rule in it — the 24-hour default, the 30-day ceiling, an
/// expired read, a comment TTL — is a comparison against "now", and none of them can be tested
/// honestly by waiting. Principle VI's mandatory retention tests are only writable because time
/// is an injected dependency rather than an ambient one.
/// </remarks>
public sealed class TestClock(DateTimeOffset? start = null) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } =
        start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan amount) => UtcNow += amount;

    public void AdvanceTo(DateTimeOffset moment) => UtcNow = moment;
}
