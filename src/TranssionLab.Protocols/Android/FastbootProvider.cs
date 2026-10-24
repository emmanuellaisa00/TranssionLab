using System.Globalization;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Protocols.Transport;

namespace TranssionLab.Protocols.Android;

public sealed class FastbootProvider : IFastbootProvider
{
    private readonly IProcessRunner _runner;
    private readonly IAppSettingsService _settings;
    private readonly IStructuredLogger _logger;
    private bool _toolVerifiedAvailable;

    public string ProviderName => "Fastboot & Fastbootd Protocol";

    public ProviderImplementationStatus ImplementationStatus =>
        _toolVerifiedAvailable
            ? ProviderImplementationStatus.SupportedValidatedWorkflows
            : ProviderImplementationStatus.Unavailable;

    public FastbootProvider(IProcessRunner runner, IAppSettingsService settings, IStructuredLogger logger)
    {
        _runner = runner;
        _settings = settings;
        _logger = logger;
    }

    private string FastbootPath => PlatformToolLocator.ResolveToolExecutable(
        _settings.Current.FastbootExecutablePath,
        "fastboot",
        _settings.Current.WorkspaceDataDirectory);

    public async Task<bool> IsToolAvailableAsync(CancellationToken ct = default)
    {
        var ver = await GetToolVersionAsync(ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(ver);
    }

    public async Task<string?> GetToolVersionAsync(CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(FastbootPath, ["--version"], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var combined = $"{res.StandardOutput}\n{res.StandardError}";
        _toolVerifiedAvailable = res.Succeeded && combined.Contains("fastboot", StringComparison.OrdinalIgnoreCase);
        if (!_toolVerifiedAvailable)
            return null;

        return combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
    }

    public async Task<IReadOnlyList<FastbootDiscoveredEndpoint>> ListDevicesAsync(CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(FastbootPath, ["devices", "-l"], TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        if (!res.Succeeded)
        {
            _toolVerifiedAvailable = false;
            return Array.Empty<FastbootDiscoveredEndpoint>();
        }

        _toolVerifiedAvailable = true;
        var initial = ParseFastbootDevicesOutput(res.StandardOutput);
        var enriched = new List<FastbootDiscoveredEndpoint>(initial.Count);

        foreach (var ep in initial)
        {
            var userspace = await GetVariableAsync(ep.Serial, "is-userspace", ct).ConfigureAwait(false);
            var actualMode = string.Equals(userspace, "yes", StringComparison.OrdinalIgnoreCase)
                ? ConnectionMode.FastbootdUserspace
                : ConnectionMode.FastbootBootloader;
            enriched.Add(ep with { Mode = actualMode });
        }

        return enriched;
    }

    public static IReadOnlyList<FastbootDiscoveredEndpoint> ParseFastbootDevicesOutput(string stdout)
    {
        var list = new List<FastbootDiscoveredEndpoint>();
        if (string.IsNullOrWhiteSpace(stdout))
            return list;

        foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = rawLine.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                continue;

            var serial = tokens[0];
            var modeToken = tokens[1].ToLowerInvariant();
            if (!modeToken.Contains("fastboot"))
                continue;

            var hint = tokens.Length > 2 ? string.Join(' ', tokens.Skip(2)) : null;
            var initialMode = modeToken == "fastbootd"
                ? ConnectionMode.FastbootdUserspace
                : ConnectionMode.FastbootBootloader;

            list.Add(new FastbootDiscoveredEndpoint(serial, initialMode, hint));
        }

        return list;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllVariablesAsync(string serial, CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(FastbootPath, ["-s", serial, "getvar", "all"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var combined = $"{res.StandardError}\n{res.StandardOutput}";
        var vars = ParseGetVarOutput(combined);

        string[] fallbackKeys = ["product", "unlocked", "secure", "is-userspace", "current-slot", "max-download-size", "serialno", "version-bootloader", "super-partition-name"];
        foreach (var key in fallbackKeys)
        {
            if (!vars.ContainsKey(key))
            {
                var single = await GetVariableAsync(serial, key, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(single))
                {
                    vars[key] = single;
                }
            }
        }

        _logger.LogInfo(ProviderName, $"fastboot -s {serial} getvar (parsed {vars.Count} variables)", null, serial, "fastboot.read_vars");
        return vars;
    }

    public static Dictionary<string, string> ParseGetVarOutput(string combinedOutput)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(combinedOutput))
            return dict;

        foreach (var rawLine in combinedOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("(bootloader)", StringComparison.OrdinalIgnoreCase))
            {
                line = line["(bootloader)".Length..].Trim();
            }

            if (line.StartsWith("Finished.", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("all:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("OKAY", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lastColon = line.LastIndexOf(':');
            if (lastColon <= 0)
                continue;

            var key = line[..lastColon].Trim();
            var val = line[(lastColon + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key) && !key.Contains(' '))
            {
                dict[key] = val;
            }
        }

        return dict;
    }

    public async Task<string?> GetVariableAsync(string serial, string variableName, CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(FastbootPath, ["-s", serial, "getvar", variableName], TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        var combined = $"{res.StandardError}\n{res.StandardOutput}";
        var prefix = $"{variableName}:";
        foreach (var rawLine in combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("(bootloader)", StringComparison.OrdinalIgnoreCase))
                line = line["(bootloader)".Length..].Trim();

            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[prefix.Length..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    public async Task<bool?> QueryUnlockAbilityAsync(string serial, CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(FastbootPath, ["-s", serial, "flashing", "get_unlock_ability"], TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        var combined = $"{res.StandardError}\n{res.StandardOutput}";
        if (combined.Contains("get_unlock_ability: 1", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("unlock_ability = 1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (combined.Contains("get_unlock_ability: 0", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("unlock_ability = 0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return null;
    }

    public async Task<CommandExecutionResult> SetActiveSlotAsync(string serial, string slotLetter, CancellationToken ct = default)
    {
        var normalized = slotLetter.Trim().ToLowerInvariant();
        if (normalized is not ("a" or "b"))
        {
            throw new ServicingException("INVALID_SLOT", "Active slot must be 'a' or 'b'.");
        }

        var res = await _runner.RunAsync(FastbootPath, ["-s", serial, $"--set-active={normalized}"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = res.Succeeded ? LogSeverity.Info : LogSeverity.Error,
            DeviceId = serial,
            ProtocolProvider = ProviderName,
            OperationId = "fastboot.set_active_slot",
            RequestedAction = $"fastboot -s {serial} --set-active={normalized}",
            CommandOutput = $"{res.StandardOutput}\n{res.StandardError}".Trim(),
            ExitCode = res.ExitCode,
            DurationMs = res.DurationMs,
            VerificationResult = res.Succeeded ? $"Slot '{normalized}' command accepted" : "Slot switch rejected"
        });
        return res;
    }

    public async Task<CommandExecutionResult> RebootAsync(string serial, string targetMode, CancellationToken ct = default)
    {
        var args = targetMode.Trim().ToLowerInvariant() switch
        {
            "bootloader" => new[] { "-s", serial, "reboot-bootloader" },
            "fastboot" or "fastbootd" => new[] { "-s", serial, "reboot", "fastboot" },
            "recovery" => new[] { "-s", serial, "reboot", "recovery" },
            _ => new[] { "-s", serial, "reboot" }
        };

        var res = await _runner.RunAsync(FastbootPath, args, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
        _logger.LogInfo(ProviderName, string.Join(' ', ["fastboot", .. args]), $"{res.StandardOutput} {res.StandardError}".Trim(), serial);
        return res;
    }

    public async Task<CommandExecutionResult> FlashPartitionAsync(string serial, string partitionName, string imagePath, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
        {
            throw new ServicingException("FLASH_IMAGE_MISSING", $"Image file does not exist: {imagePath}");
        }

        var res = await _runner.RunAsync(
            FastbootPath,
            ["-s", serial, "flash", partitionName, imagePath],
            TimeSpan.FromMinutes(10),
            ct).ConfigureAwait(false);

        var combined = $"{res.StandardOutput}\n{res.StandardError}".Trim();
        var ok = res.Succeeded && !combined.Contains("FAILED (", StringComparison.OrdinalIgnoreCase);

        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = ok ? LogSeverity.Info : LogSeverity.Error,
            DeviceId = serial,
            ProtocolProvider = ProviderName,
            OperationId = "flash.fastboot_partition",
            RequestedAction = $"fastboot -s {serial} flash {partitionName} {Path.GetFileName(imagePath)}",
            CommandOutput = combined,
            ErrorMessage = ok ? null : combined,
            ExitCode = res.ExitCode,
            DurationMs = res.DurationMs,
            VerificationResult = ok ? "OKAY received from fastboot target" : "Flash command failed"
        });

        return res with { Succeeded = ok };
    }

    public async Task<CommandExecutionResult> FlashVbmetaAsync(
        string serial,
        string partitionName,
        string vbmetaImagePath,
        bool disableVerity,
        bool disableVerification,
        CancellationToken ct = default)
    {
        if (!File.Exists(vbmetaImagePath))
        {
            throw new ServicingException("VBMETA_IMAGE_MISSING", $"vbmeta image does not exist: {vbmetaImagePath}");
        }

        var args = new List<string> { "-s", serial };
        if (disableVerity) args.Add("--disable-verity");
        if (disableVerification) args.Add("--disable-verification");
        args.AddRange(["flash", partitionName, vbmetaImagePath]);

        var res = await _runner.RunAsync(FastbootPath, args, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        var combined = $"{res.StandardOutput}\n{res.StandardError}".Trim();
        var ok = res.Succeeded && !combined.Contains("FAILED (", StringComparison.OrdinalIgnoreCase);

        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = ok ? LogSeverity.Info : LogSeverity.Error,
            DeviceId = serial,
            ProtocolProvider = ProviderName,
            OperationId = "flash.fastboot_partition",
            RequestedAction = $"fastboot {string.Join(' ', args.Select(a => a == vbmetaImagePath ? Path.GetFileName(a) : a))}",
            CommandOutput = combined,
            ErrorMessage = ok ? null : combined,
            ExitCode = res.ExitCode,
            DurationMs = res.DurationMs,
            VerificationResult = ok ? "vbmeta flashed successfully" : "vbmeta flash failed"
        });

        return res with { Succeeded = ok };
    }

    public async Task<CommandExecutionResult> CreateLogicalPartitionAsync(
        string serial,
        string partitionName,
        long sizeBytes,
        CancellationToken ct = default)
    {
        var sizeStr = sizeBytes.ToString(CultureInfo.InvariantCulture);
        var res = await _runner.RunAsync(
            FastbootPath,
            ["-s", serial, "create-logical-partition", partitionName, sizeStr],
            TimeSpan.FromSeconds(30),
            ct).ConfigureAwait(false);

        return LogAndNormalizeFastbootMutation(
            serial,
            "partition.manage_logical_super",
            $"fastboot -s {serial} create-logical-partition {partitionName} {sizeStr}",
            res);
    }

    public async Task<CommandExecutionResult> ResizeLogicalPartitionAsync(
        string serial,
        string partitionName,
        long newSizeBytes,
        CancellationToken ct = default)
    {
        var sizeStr = newSizeBytes.ToString(CultureInfo.InvariantCulture);
        var res = await _runner.RunAsync(
            FastbootPath,
            ["-s", serial, "resize-logical-partition", partitionName, sizeStr],
            TimeSpan.FromSeconds(30),
            ct).ConfigureAwait(false);

        return LogAndNormalizeFastbootMutation(
            serial,
            "partition.manage_logical_super",
            $"fastboot -s {serial} resize-logical-partition {partitionName} {sizeStr}",
            res);
    }

    public async Task<CommandExecutionResult> DeleteLogicalPartitionAsync(
        string serial,
        string partitionName,
        CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(
            FastbootPath,
            ["-s", serial, "delete-logical-partition", partitionName],
            TimeSpan.FromSeconds(30),
            ct).ConfigureAwait(false);

        return LogAndNormalizeFastbootMutation(
            serial,
            "partition.manage_logical_super",
            $"fastboot -s {serial} delete-logical-partition {partitionName}",
            res);
    }

    public async Task<CommandExecutionResult> ErasePartitionAsync(
        string serial,
        string partitionName,
        CancellationToken ct = default)
    {
        var res = await _runner.RunAsync(
            FastbootPath,
            ["-s", serial, "erase", partitionName],
            TimeSpan.FromMinutes(3),
            ct).ConfigureAwait(false);

        return LogAndNormalizeFastbootMutation(
            serial,
            "partition.erase_safe",
            $"fastboot -s {serial} erase {partitionName}",
            res);
    }

    private CommandExecutionResult LogAndNormalizeFastbootMutation(
        string serial,
        string operationId,
        string actionDisplay,
        CommandExecutionResult res)
    {
        var combined = $"{res.StandardOutput}\n{res.StandardError}".Trim();
        var ok = res.Succeeded && !combined.Contains("FAILED (", StringComparison.OrdinalIgnoreCase);

        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = ok ? LogSeverity.Info : LogSeverity.Error,
            DeviceId = serial,
            ProtocolProvider = ProviderName,
            OperationId = operationId,
            RequestedAction = actionDisplay,
            CommandOutput = combined,
            ErrorMessage = ok ? null : combined,
            ExitCode = res.ExitCode,
            DurationMs = res.DurationMs,
            VerificationResult = ok ? "OKAY received from fastboot target" : "Fastboot command rejected"
        });

        return res with { Succeeded = ok };
    }

    public async Task<CommandExecutionResult> ExecuteVerifiedUnlockCommandAsync(string serial, bool useLegacyOemCommand, CancellationToken ct = default)
    {
        var args = useLegacyOemCommand
            ? new[] { "-s", serial, "oem", "unlock" }
            : new[] { "-s", serial, "flashing", "unlock" };

        var res = await _runner.RunAsync(FastbootPath, args, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        var combined = $"{res.StandardOutput}\n{res.StandardError}".Trim();
        var ok = res.Succeeded && !combined.Contains("FAILED (", StringComparison.OrdinalIgnoreCase);

        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = ok ? LogSeverity.Info : LogSeverity.Error,
            DeviceId = serial,
            ProtocolProvider = ProviderName,
            OperationId = "bootloader.verified_unlock",
            RequestedAction = string.Join(' ', ["fastboot", .. args]),
            CommandOutput = combined,
            ErrorMessage = ok ? null : combined,
            ExitCode = res.ExitCode,
            DurationMs = res.DurationMs,
            VerificationResult = ok ? "Unlock command accepted; awaiting getvar verification" : "Unlock command rejected by bootloader"
        });

        return res with { Succeeded = ok };
    }
}
