using TranssionLab.Core.Enums;

namespace TranssionLab.Core.Models;

/// <summary>
/// Wraps any reported field with explicit provenance (Detected, Inferred, Verified, Unknown)
/// so the UI and logs never conflate guessed data with verified hardware state.
/// </summary>
public sealed record TrackedValue<T>(
    T? Value,
    DataProvenance Provenance,
    string Source,
    DateTimeOffset ObservedAtUtc,
    string? Notes = null)
{
    public static TrackedValue<T> Unknown(string reason = "Not yet queried") =>
        new(default, DataProvenance.Unknown, reason, DateTimeOffset.UtcNow);

    public static TrackedValue<T> Detected(T value, string source, string? notes = null) =>
        new(value, DataProvenance.Detected, source, DateTimeOffset.UtcNow, notes);

    public static TrackedValue<T> Inferred(T value, string source, string? notes = null) =>
        new(value, DataProvenance.Inferred, source, DateTimeOffset.UtcNow, notes);

    public static TrackedValue<T> Verified(T value, string source, string? notes = null) =>
        new(value, DataProvenance.Verified, source, DateTimeOffset.UtcNow, notes);

    public string DisplayText => Value is null || (Value is string s && string.IsNullOrWhiteSpace(s))
        ? "Unknown"
        : Value.ToString()!;

    public string BadgeText => Provenance switch
    {
        DataProvenance.Verified => "VERIFIED",
        DataProvenance.Detected => "DETECTED",
        DataProvenance.Inferred => "INFERRED",
        _ => "UNKNOWN"
    };
}
