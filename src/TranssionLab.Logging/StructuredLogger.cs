using System.Text;
using System.Text.Json;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Logging;

/// <summary>
/// Central thread-safe structured logger (Section 8).
/// Maintains both an immutable session archive (never cleared by ClearDisplayOnly) and a live UI display buffer.
/// </summary>
public sealed class StructuredLogger : IStructuredLogger
{
    private static readonly JsonSerializerOptions JsonLineOpts = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions JsonPrettyOpts = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly List<StructuredLogEntry> _archivedEntries = new();
    private readonly List<StructuredLogEntry> _displayEntries = new();
    private readonly string _archiveFilePath;
    private readonly Func<bool> _redactionEnabled;
    private long _sequence;

    public string CurrentSessionId { get; }
    public event EventHandler<StructuredLogEntry>? EntryRecorded;

    public StructuredLogger(string? logDirectory = null, Func<bool>? redactionEnabled = null)
    {
        CurrentSessionId = $"SES-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        _redactionEnabled = redactionEnabled ?? (() => true);

        var dir = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TranssionLab",
            "logs");
        Directory.CreateDirectory(dir);
        _archiveFilePath = Path.Combine(dir, $"{CurrentSessionId}.jsonl");
    }

    public void Log(StructuredLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var shouldRedact = _redactionEnabled();

        StructuredLogEntry sanitized;
        lock (_gate)
        {
            var seq = ++_sequence;
            sanitized = entry with
            {
                SessionId = string.IsNullOrWhiteSpace(entry.SessionId) ? CurrentSessionId : entry.SessionId,
                SequenceNumber = seq,
                RequestedAction = shouldRedact ? (LogRedactor.Redact(entry.RequestedAction) ?? string.Empty) : entry.RequestedAction,
                CommandOutput = shouldRedact ? LogRedactor.Redact(entry.CommandOutput) : entry.CommandOutput,
                ErrorMessage = shouldRedact ? LogRedactor.Redact(entry.ErrorMessage) : entry.ErrorMessage
            };

            _archivedEntries.Add(sanitized);
            _displayEntries.Add(sanitized);

            try
            {
                var line = JsonSerializer.Serialize(sanitized, JsonLineOpts) + Environment.NewLine;
                File.AppendAllText(_archiveFilePath, line, Encoding.UTF8);
            }
            catch
            {
                // Keep logging non-fatal even if disk becomes full
            }
        }

        EntryRecorded?.Invoke(this, sanitized);
    }

    public void LogInfo(string provider, string action, string? output = null, string? deviceId = null, string? operationId = null) =>
        Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = CurrentSessionId,
            SequenceNumber = 0,
            Severity = LogSeverity.Info,
            DeviceId = deviceId,
            ProtocolProvider = provider,
            OperationId = operationId,
            RequestedAction = action,
            CommandOutput = output
        });

    public void LogWarning(string provider, string action, string message, string? deviceId = null, string? operationId = null) =>
        Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = CurrentSessionId,
            SequenceNumber = 0,
            Severity = LogSeverity.Warning,
            DeviceId = deviceId,
            ProtocolProvider = provider,
            OperationId = operationId,
            RequestedAction = action,
            ErrorMessage = message
        });

    public void LogError(string provider, string action, string errorMessage, int? exitCode = null, string? deviceId = null, string? operationId = null) =>
        Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = CurrentSessionId,
            SequenceNumber = 0,
            Severity = LogSeverity.Error,
            DeviceId = deviceId,
            ProtocolProvider = provider,
            OperationId = operationId,
            RequestedAction = action,
            ErrorMessage = errorMessage,
            ExitCode = exitCode
        });

    public IReadOnlyList<StructuredLogEntry> GetDisplayEntries(
        string? searchQuery = null,
        LogSeverity? minSeverity = null,
        string? providerFilter = null)
    {
        lock (_gate)
        {
            IEnumerable<StructuredLogEntry> query = _displayEntries;

            if (minSeverity.HasValue)
            {
                query = query.Where(e => e.Severity >= minSeverity.Value);
            }

            if (!string.IsNullOrWhiteSpace(providerFilter) && !string.Equals(providerFilter, "All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(e => string.Equals(e.ProtocolProvider, providerFilter, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                var term = searchQuery.Trim();
                query = query.Where(e =>
                    e.RequestedAction.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (e.CommandOutput?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.ErrorMessage?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.DeviceId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.OperationId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    e.ProtocolProvider.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            return query.ToList();
        }
    }

    public IReadOnlyList<StructuredLogEntry> GetArchivedSessionEntries()
    {
        lock (_gate)
        {
            return _archivedEntries.ToList();
        }
    }

    /// <summary>
    /// Clears only the live display buffer without deleting archived session logs (Section 8).
    /// </summary>
    public void ClearDisplayOnly()
    {
        lock (_gate)
        {
            _displayEntries.Clear();
        }
    }

    public async Task ExportTxtAsync(string filePath, CancellationToken ct = default)
    {
        var entries = GetArchivedSessionEntries();
        var sb = new StringBuilder();
        sb.AppendLine($"=== TranssionLab Session Log Export ({CurrentSessionId}) ===");
        sb.AppendLine($"Generated UTC: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Total Archived Entries: {entries.Count}");
        sb.AppendLine(new string('=', 72));

        foreach (var entry in entries)
        {
            sb.AppendLine($"[{entry.TimestampUtc:yyyy-MM-dd HH:mm:ss.fff} UTC] #{entry.SequenceNumber:D4} [{entry.Severity.ToString().ToUpperInvariant()}] [{entry.ProtocolProvider}] {entry.RequestedAction}");
            if (!string.IsNullOrWhiteSpace(entry.DeviceId))
                sb.AppendLine($"  Device: {entry.DeviceId} | USB: {entry.UsbInterface ?? "N/A"} | Op: {entry.OperationId ?? "N/A"}");
            if (entry.PrerequisiteChecks.Count > 0)
                sb.AppendLine($"  Prerequisites: {string.Join("; ", entry.PrerequisiteChecks)}");
            if (!string.IsNullOrWhiteSpace(entry.CommandOutput))
                sb.AppendLine($"  Output: {entry.CommandOutput}");
            if (!string.IsNullOrWhiteSpace(entry.ErrorMessage))
                sb.AppendLine($"  Error (Exit={entry.ExitCode?.ToString() ?? "N/A"}): {entry.ErrorMessage}");
            if (!string.IsNullOrWhiteSpace(entry.VerificationResult))
                sb.AppendLine($"  Verification ({entry.DurationMs ?? 0} ms): {entry.VerificationResult}");
        }

        var fullPath = Path.GetFullPath(filePath);
        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parentDir))
            Directory.CreateDirectory(parentDir);
        await File.WriteAllTextAsync(fullPath, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    public async Task ExportJsonAsync(string filePath, CancellationToken ct = default)
    {
        var entries = GetArchivedSessionEntries();
        var json = JsonSerializer.Serialize(entries, JsonPrettyOpts);
        var fullPath = Path.GetFullPath(filePath);
        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parentDir))
            Directory.CreateDirectory(parentDir);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    public string BuildTroubleshootingBundle(
        IReadOnlyList<ConnectedDeviceSnapshot> devices,
        IReadOnlyList<ProtocolCapabilitySummary> capabilities,
        IReadOnlyList<UsbConnectionHistoryEvent> usbHistory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TRANSSIONLAB DIAGNOSTIC TROUBLESHOOTING REPORT");
        sb.AppendLine($"Session ID   : {CurrentSessionId}");
        sb.AppendLine($"Timestamp UTC: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Host OS      : {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET Runtime : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine();

        sb.AppendLine("--- PROTOCOL PROVIDERS ---");
        foreach (var cap in capabilities)
        {
            sb.AppendLine($"- {cap.ProviderName}: Status={cap.ImplementationStatus}, Verification={cap.VerificationLevel}, Dependencies={cap.DependencySummary}");
        }

        sb.AppendLine();
        sb.AppendLine($"--- CONNECTED DEVICES ({devices.Count}) ---");
        foreach (var dev in devices)
        {
            sb.AppendLine($"- Key={dev.DeviceKey} | Mode={dev.ActiveConnectionMode} | State={dev.DashboardState} | Model={dev.ExactModel.DisplayText} [{dev.ExactModel.BadgeText}] | Chipset={dev.ChipsetFamily.DisplayText} [{dev.ChipsetFamily.BadgeText}] | USB={dev.UsbIdsDisplay}");
        }

        sb.AppendLine();
        sb.AppendLine($"--- RECENT USB EVENTS ({Math.Min(15, usbHistory.Count)}) ---");
        foreach (var evt in usbHistory.TakeLast(15))
        {
            sb.AppendLine($"- [{evt.TimestampUtc:HH:mm:ss}] {evt.EventKind}: {evt.VidPid} ({evt.FriendlyName}) -> {evt.Mode} | {evt.Details}");
        }

        sb.AppendLine();
        var recentLogs = GetArchivedSessionEntries().TakeLast(25).ToList();
        sb.AppendLine($"--- RECENT STRUCTURED LOGS ({recentLogs.Count}) ---");
        foreach (var log in recentLogs)
        {
            sb.AppendLine($"- [{log.TimestampUtc:HH:mm:ss}] [{log.Severity}] {log.ProtocolProvider}: {log.RequestedAction} {log.ErrorMessage ?? log.VerificationResult ?? string.Empty}");
        }

        return sb.ToString();
    }
}
