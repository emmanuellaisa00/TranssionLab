using TranssionLab.Core.Enums;

namespace TranssionLab.Core.Models;

/// <summary>
/// Central structured log record covering every field mandated in Section 8.
/// </summary>
public sealed record StructuredLogEntry
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string SessionId { get; init; }
    public required long SequenceNumber { get; init; }
    public required LogSeverity Severity { get; init; }
    public string? DeviceId { get; init; }
    public required string ProtocolProvider { get; init; }
    public string? UsbInterface { get; init; }
    public string? OperationId { get; init; }
    public required string RequestedAction { get; init; }
    public IReadOnlyList<string> PrerequisiteChecks { get; init; } = Array.Empty<string>();
    public string? CommandOutput { get; init; }
    public string? ErrorMessage { get; init; }
    public int? ExitCode { get; init; }
    public string? ConnectionChange { get; init; }
    public long? DurationMs { get; init; }
    public string? VerificationResult { get; init; }
}

public sealed record OperationHistoryRecord(
    string OperationId,
    string SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMs,
    string Category,
    string OperationName,
    string? DeviceId,
    string ProtocolProvider,
    bool Succeeded,
    bool WasDryRun,
    string VerificationResult,
    string Summary,
    string? ReportFilePath);

public sealed record AppSettingsModel
{
    public string AdbExecutablePath { get; set; } = "adb";
    public string FastbootExecutablePath { get; set; } = "fastboot";
    public string? OptionalMetaLibraryPath { get; set; }
    public string WorkspaceDataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TranssionLab");
    public bool AutoPollUsbChanges { get; set; } = true;
    public int UsbPollIntervalSeconds { get; set; } = 3;
    public bool RedactSensitiveDataInLogs { get; set; } = true;
    public bool RequireStrictProfileMatchBeforeFlash { get; set; } = true;
}
