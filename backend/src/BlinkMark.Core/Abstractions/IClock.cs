namespace BlinkMark.Core.Abstractions;

/// <summary>Supplies the current time.</summary>
/// <remarks>
/// Retention is the heart of this product, and every rule in it is a comparison against "now".
/// Testing a 30-day ceiling, a 24-hour default, or an expired read is impossible if "now" is
/// ambient, so it is a dependency everywhere instead.
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
