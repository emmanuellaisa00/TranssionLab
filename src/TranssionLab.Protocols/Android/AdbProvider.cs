using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Protocols.Transport;

namespace TranssionLab.Protocols.Android;

public sealed partial class AdbProvider : IAdbProvider
{
    private readonly IProcessRunner _runner;
    private readonly IAppSettingsService _settings;
    private readonly IStructuredLogger _logger;
    private bool _toolVerifiedAvailable;

    public string ProviderName => "Android Debug Bridge (ADB)";

    public ProviderImplementationStatus ImplementationStatus =>
        _toolVerifiedAvailable
            ? ProviderImplementationStatus.SupportedValidatedWorkflows
            : ProviderImplementationStatus.Unavailable;

    [GeneratedRegex(@"^\[([^\]]+)\]:\s*\[(.*)\]$")]
    private static partial Regex GetPropLineRegex();

    public AdbProvider(IProcessRunner runner, IAppSettingsService settings, IStructuredLogger logger)
    {
        _runner = runner;
        _settings = settings;
        _logger = logger;
    }

    private string AdbPath => PlatformToolLocator.ResolveToolExecutable(
        _settings.Current.AdbExecutablePath,
        "adb",
        _settings.Current.WorkspaceDataDirectory);

    public async Task<bool> IsToolAvailableAsync(CancellationToken ct = default)
    {
        var ver = await GetToolVersionAsync(ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(ver);
    }

    public async Task<string?> GetToolVersionAsync(CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(AdbPath, ["version"], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        _toolVerifiedAvailable = res.Succeeded && res.StandardOutput.Contains("Android Debug Bridge", StringComparison.OrdinalIgnoreCase);
        if (!_toolVerifiedAvailable)
        {
            return null;
        }

        var firstLine = res.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstLine?.Trim();
    }

    public async Task<IReadOnlyList<AdbDiscoveredEndpoint>> ListDevicesAsync(CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(AdbPath, ["devices", "-l"], TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        if (!res.Succeeded)
        {
            _toolVerifiedAvailable = false;
            return Array.Empty<AdbDiscoveredEndpoint>();
        }

        _toolVerifiedAvailable = true;
        return ParseAdbDevicesOutput(res.StandardOutput);
    }

    public static IReadOnlyList<AdbDiscoveredEndpoint> ParseAdbDevicesOutput(string stdout)
    {
        var list = new List<AdbDiscoveredEndpoint>();
        if (string.IsNullOrWhiteSpace(stdout))
            return list;

        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith('*'))
            {
                continue;
            }

            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                continue;

            var serial = tokens[0];
            var rawState = tokens[1].ToLowerInvariant();
            var state = rawState switch
            {
                "device" => AdbDeviceState.Connected,
                "unauthorized" => AdbDeviceState.Unauthorized,
                "authorizing" or "connecting" => AdbDeviceState.Authorizing,
                "offline" => AdbDeviceState.Offline,
                "recovery" => AdbDeviceState.Recovery,
                "sideload" => AdbDeviceState.Sideload,
                _ => AdbDeviceState.Unavailable
            };

            string? product = null, model = null, device = null, transportId = null;
            for (int i = 2; i < tokens.Length; i++)
            {
                var kv = tokens[i].Split(':', 2);
                if (kv.Length != 2) continue;
                switch (kv[0].ToLowerInvariant())
                {
                    case "product": product = kv[1]; break;
                    case "model": model = kv[1].Replace('_', ' '); break;
                    case "device": device = kv[1]; break;
                    case "transport_id": transportId = kv[1]; break;
                }
            }

            list.Add(new AdbDiscoveredEndpoint(serial, state, product, model, device, transportId));
        }

        return list;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDevicePropertiesAsync(string serial, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var res = await _runner.RunAsync(AdbPath, ["-s", serial, "shell", "getprop"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = res.Succeeded ? LogSeverity.Info : LogSeverity.Warning,
            DeviceId = serial,
            ProtocolProvider = ProviderName,
            OperationId = "adb.read_identity",
            RequestedAction = $"adb -s {serial} shell getprop",
            ExitCode = res.ExitCode,
            DurationMs = res.DurationMs,
            ErrorMessage = res.Succeeded ? null : res.StandardError,
            VerificationResult = res.Succeeded ? "Properties retrieved" : "Property query failed"
        });

        if (!res.Succeeded)
        {
            return new Dictionary<string, string>();
        }

        return ParseGetPropOutput(res.StandardOutput);
    }

    public static IReadOnlyDictionary<string, string> ParseGetPropOutput(string stdout)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stdout))
            return dict;

        foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = GetPropLineRegex().Match(rawLine.Trim());
            if (m.Success)
            {
                dict[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
            }
        }

        return dict;
    }

    public async Task<string> CaptureBatterySummaryAsync(string serial, CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(AdbPath, ["-s", serial, "shell", "dumpsys", "battery"], TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        _logger.LogInfo(ProviderName, $"adb -s {serial} shell dumpsys battery", res.Succeeded ? "OK" : res.StandardError, serial, "adb.diagnostics_health");
        return res.Succeeded ? res.StandardOutput.Trim() : $"Battery query failed: {res.StandardError}";
    }

    public async Task<string> CaptureStorageSummaryAsync(string serial, CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(AdbPath, ["-s", serial, "shell", "df", "-h"], TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        _logger.LogInfo(ProviderName, $"adb -s {serial} shell df -h", res.Succeeded ? "OK" : res.StandardError, serial, "adb.diagnostics_health");
        return res.Succeeded ? res.StandardOutput.Trim() : $"Storage query failed: {res.StandardError}";
    }

    public async Task<IReadOnlyList<AdbBlockPartitionEntry>> EnumerateBlockPartitionsAsync(string serial, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);

        var uidRes = await _runner.RunAsync(AdbPath, ["-s", serial, "shell", "id -u"], TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        bool isRootShell = uidRes.Succeeded && uidRes.StandardOutput.Trim() == "0";

        var procRes = await _runner.RunAsync(AdbPath, ["-s", serial, "shell", "cat /proc/partitions"], TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        var blockSizes = ParseProcPartitions(procRes.StandardOutput);

        var byNameRes = await _runner.RunAsync(
            AdbPath,
            ["-s", serial, "shell", "ls -la /dev/block/by-name 2>/dev/null || ls -la /dev/block/platform/*/by-name 2>/dev/null"],
            TimeSpan.FromSeconds(10),
            ct).ConfigureAwait(false);

        var entries = ParseByNameListing(byNameRes.StandardOutput, blockSizes, isRootShell);
        _logger.LogInfo(
            ProviderName,
            $"adb -s {serial} enumerate /dev/block/by-name (found {entries.Count} partitions, rootShell={isRootShell})",
            null,
            serial,
            "partition.inspect_table");
        return entries;
    }

    public static Dictionary<string, long> ParseProcPartitions(string stdout)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stdout))
            return map;

        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = raw.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 4 &&
                long.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var blocks1K))
            {
                map[tokens[3]] = blocks1K * 1024L;
            }
        }
        return map;
    }

    public static List<AdbBlockPartitionEntry> ParseByNameListing(
        string lsOutput,
        IReadOnlyDictionary<string, long> procPartitionBytes,
        bool isRootOrRecoveryShell)
    {
        var list = new List<AdbBlockPartitionEntry>();
        if (string.IsNullOrWhiteSpace(lsOutput))
            return list;

        foreach (var rawLine in lsOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            int arrowIdx = line.IndexOf("->", StringComparison.Ordinal);
            if (arrowIdx <= 0)
                continue;

            var left = line[..arrowIdx].Trim();
            var targetNode = line[(arrowIdx + 2)..].Trim();
            var leftTokens = left.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (leftTokens.Length == 0)
                continue;

            var partName = leftTokens[^1].Trim();
            if (string.IsNullOrWhiteSpace(partName) || partName is "." or "..")
                continue;

            var shortDevName = Path.GetFileName(targetNode);
            long? bytes = procPartitionBytes.TryGetValue(shortDevName, out var b) ? b : null;

            list.Add(new AdbBlockPartitionEntry(
                partName,
                $"/dev/block/by-name/{partName}",
                targetNode,
                bytes,
                isRootOrRecoveryShell));
        }

        return list.OrderBy(p => p.PartitionName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<(CommandExecutionResult Result, long BytesRead, string? Sha256Hex)> ReadbackPartitionToFileAsync(
        string serial,
        string blockNodePath,
        string localDestinationPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockNodePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDestinationPath);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localDestinationPath))!);
        if (File.Exists(localDestinationPath))
        {
            File.Delete(localDestinationPath);
        }

        var res = await _runner.RunAsync(
            AdbPath,
            ["-s", serial, "pull", blockNodePath, localDestinationPath],
            TimeSpan.FromMinutes(15),
            ct).ConfigureAwait(false);

        long bytes = 0;
        string? shaHex = null;
        bool ok = res.Succeeded && File.Exists(localDestinationPath);
        if (ok)
        {
            var fi = new FileInfo(localDestinationPath);
            bytes = fi.Length;
            if (bytes == 0)
            {
                ok = false;
                File.Delete(localDestinationPath);
            }
            else if (bytes <= 1024L * 1024 * 1024)
            {
                await using var fs = File.OpenRead(localDestinationPath);
                var hashBytes = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                shaHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
        }

        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = ok ? LogSeverity.Info : LogSeverity.Warning,
            DeviceId = serial,
            ProtocolProvider = ProviderName,
            OperationId = "partition.adb_readback",
            RequestedAction = $"adb -s {serial} pull {blockNodePath} \"{Path.GetFileName(localDestinationPath)}\"",
            CommandOutput = $"{res.StandardOutput}\n{res.StandardError}".Trim(),
            ErrorMessage = ok ? null : $"{res.StandardOutput} {res.StandardError}".Trim(),
            ExitCode = res.ExitCode,
            DurationMs = res.DurationMs,
            VerificationResult = ok
                ? $"Readback verified ({bytes:N0} B, SHA256={shaHex ?? "SKIPPED_OVER_1GB"})"
                : "Readback denied or failed (SELinux restricts raw block nodes on non-root retail user builds; use Recovery or authorized root shell)"
        });

        return (res with { Succeeded = ok }, bytes, shaHex);
    }

    public async Task<CommandExecutionResult> RebootAsync(string serial, string targetMode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var args = string.IsNullOrWhiteSpace(targetMode) || string.Equals(targetMode, "system", StringComparison.OrdinalIgnoreCase)
            ? new[] { "-s", serial, "reboot" }
            : new[] { "-s", serial, "reboot", targetMode.Trim().ToLowerInvariant() };

        var res = await _runner.RunAsync(AdbPath, args, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = res.Succeeded ? LogSeverity.Info : LogSeverity.Error,
            DeviceId = serial,
            ProtocolProvider = ProviderName,
            OperationId = "adb.reboot_mode",
            RequestedAction = string.Join(' ', ["adb", .. args]),
            CommandOutput = res.StandardOutput,
            ErrorMessage = res.Succeeded ? null : res.StandardError,
            ExitCode = res.ExitCode,
            DurationMs = res.DurationMs,
            VerificationResult = res.Succeeded ? $"Reboot to {targetMode} dispatched" : "Reboot command failed"
        });
        return res;
    }

    public async Task<CommandExecutionResult> SideloadPackageAsync(string serial, string otaZipPath, CancellationToken ct = default)
    {
        if (!File.Exists(otaZipPath))
        {
            throw new ServicingException("OTA_FILE_MISSING", $"OTA package not found: {otaZipPath}");
        }

        using (var fs = File.OpenRead(otaZipPath))
        {
            var magic = new byte[4];
            if (await fs.ReadAsync(magic, ct).ConfigureAwait(false) < 4 || magic[0] != 0x50 || magic[1] != 0x4B)
            {
                throw new ServicingException("INVALID_OTA_ARCHIVE", "Selected file is not a valid ZIP OTA container.");
            }
        }

        var res = await _runner.RunAsync(AdbPath, ["-s", serial, "sideload", otaZipPath], TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = res.Succeeded ? LogSeverity.Info : LogSeverity.Error,
            DeviceId = serial,
            ProtocolProvider = ProviderName,
            OperationId = "recovery.ota_sideload",
            RequestedAction = $"adb -s {serial} sideload {Path.GetFileName(otaZipPath)}",
            CommandOutput = res.StandardOutput,
            ErrorMessage = res.Succeeded ? null : res.StandardError,
            ExitCode = res.ExitCode,
            DurationMs = res.DurationMs,
            VerificationResult = res.Succeeded ? "Sideload transfer completed" : "Sideload failed or rejected by recovery verifier"
        });

        return res;
    }
}

public sealed class RecoveryProvider : IRecoveryProvider
{
    private readonly IAdbProvider _adbProvider;

    public string ProviderName => "Android Recovery & Sideload";
    public ProviderImplementationStatus ImplementationStatus => _adbProvider.ImplementationStatus;

    public RecoveryProvider(IAdbProvider adbProvider)
    {
        _adbProvider = adbProvider;
    }

    public async Task<IReadOnlyList<AdbDiscoveredEndpoint>> DetectRecoveryAndSideloadEndpointsAsync(CancellationToken ct = default)
    {
        var all = await _adbProvider.ListDevicesAsync(ct).ConfigureAwait(false);
        return all.Where(d => d.State is AdbDeviceState.Recovery or AdbDeviceState.Sideload).ToList();
    }

    public Task<CommandExecutionResult> VerifyAndSideloadOtaAsync(string serial, string otaPackagePath, CancellationToken ct = default) =>
        _adbProvider.SideloadPackageAsync(serial, otaPackagePath, ct);
}
