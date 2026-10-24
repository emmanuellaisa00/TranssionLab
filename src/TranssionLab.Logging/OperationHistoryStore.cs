using System.Text;
using System.Text.Json;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Logging;

public sealed class OperationHistoryStore : IOperationHistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _historyFilePath;
    private readonly List<OperationHistoryRecord> _records = new();

    public OperationHistoryStore(string? storageDirectory = null)
    {
        var dir = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TranssionLab",
            "history");
        Directory.CreateDirectory(dir);
        _historyFilePath = Path.Combine(dir, "operations_history.json");
        LoadExisting();
    }

    private void LoadExisting()
    {
        try
        {
            if (File.Exists(_historyFilePath))
            {
                var json = File.ReadAllText(_historyFilePath, Encoding.UTF8);
                var items = JsonSerializer.Deserialize<List<OperationHistoryRecord>>(json, JsonOpts);
                if (items is not null)
                {
                    _records.AddRange(items);
                }
            }
        }
        catch
        {
            // Keep clean empty history if corrupted
        }
    }

    public IReadOnlyList<OperationHistoryRecord> GetRecords()
    {
        lock (_gate)
        {
            return _records.OrderByDescending(r => r.StartedAtUtc).ToList();
        }
    }

    public async Task RecordAsync(OperationHistoryRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        string serialized;
        lock (_gate)
        {
            _records.Add(record);
            serialized = JsonSerializer.Serialize(_records, JsonOpts);
        }

        await File.WriteAllTextAsync(_historyFilePath, serialized, Encoding.UTF8, ct).ConfigureAwait(false);
    }
}

public static class CrashReportService
{
    public static string WriteCrashReport(Exception exception, string sessionId, string? crashDirectory = null)
    {
        try
        {
            var dir = crashDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TranssionLab",
                "logs",
                "crashes");
            Directory.CreateDirectory(dir);

            var fileName = $"crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.txt";
            var path = Path.Combine(dir, fileName);

            var report = new StringBuilder()
                .AppendLine("TRANSSIONLAB LOCAL CRASH REPORT")
                .AppendLine($"Timestamp UTC: {DateTimeOffset.UtcNow:O}")
                .AppendLine($"Session ID   : {sessionId}")
                .AppendLine($"OS           : {Environment.OSVersion}")
                .AppendLine($"Exception    : {exception.GetType().FullName}")
                .AppendLine($"Message      : {LogRedactor.Redact(exception.Message)}")
                .AppendLine("Stack Trace:")
                .AppendLine(LogRedactor.Redact(exception.ToString()))
                .ToString();

            File.WriteAllText(path, report, Encoding.UTF8);
            return path;
        }
        catch
        {
            return "(Crash report file could not be written due to local disk permissions)";
        }
    }
}
