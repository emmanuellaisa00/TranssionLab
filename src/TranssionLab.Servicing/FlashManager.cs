using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Firmware;

namespace TranssionLab.Servicing;

/// <summary>
/// Safe, capability-aware 11-step Flash Manager (Section 7).
/// Never assumes cross-chipset command compatibility, never auto-erases userdata,
/// blocks protected NVRAM/calibration partitions, and requires dry-run + explicit confirmation before writing.
/// </summary>
public sealed class FlashManager : IFlashManager
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly HashSet<string> KnownLogicalPartitionPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "vendor", "product", "system_ext", "odm", "vendor_dlkm", "system_dlkm", "odm_dlkm", "tr_mi", "tr_theme", "tr_region", "tr_company", "tr_carrier"
    };

    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IDeviceProfileRepository _profileRepo;
    private readonly IFastbootProvider _fastboot;
    private readonly IFirmwareFormatRegistry _firmwareRegistry;
    private readonly IAppSettingsService _settings;
    private readonly IStructuredLogger _logger;
    private readonly IOperationHistoryStore _historyStore;

    public FlashManager(
        IDeviceDiscoveryProvider discovery,
        IDeviceProfileRepository profileRepo,
        IFastbootProvider fastboot,
        IFirmwareFormatRegistry firmwareRegistry,
        IAppSettingsService settings,
        IStructuredLogger logger,
        IOperationHistoryStore historyStore)
    {
        _discovery = discovery;
        _profileRepo = profileRepo;
        _fastboot = fastboot;
        _firmwareRegistry = firmwareRegistry;
        _settings = settings;
        _logger = logger;
        _historyStore = historyStore;
    }

    public async Task<FlashExecutionPlan> BuildExecutionPlanAsync(
        ConnectedDeviceSnapshot device,
        IReadOnlyList<(string PartitionName, string ImageFilePath)> requestedPartitions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(requestedPartitions);

        if (requestedPartitions.Count == 0)
        {
            throw new ServicingException("EMPTY_FLASH_REQUEST", "Select at least one partition and image file to build a flash plan.");
        }

        // Step 1: Ensure explicit physical device selection if multiple devices are attached
        if (_discovery.RequiresExplicitSelection)
        {
            throw new AmbiguousDeviceSelectionException(_discovery.ConnectedDevices.Count);
        }

        var safetyFindings = new List<string>();
        bool blocked = false;

        // Step 2: Verify device profile
        var profile = !string.IsNullOrWhiteSpace(device.MatchedProfileId)
            ? _profileRepo.GetById(device.MatchedProfileId)
            : null;

        if (profile is null && _settings.Current.RequireStrictProfileMatchBeforeFlash)
        {
            blocked = true;
            safetyFindings.Add("BLOCKED: Device does not match a verified TECNO/Infinix/itel Device Profile. Arbitrary flashing based on filenames alone is prohibited.");
        }

        // Step 3: Verify active protocol
        if (device.ActiveConnectionMode is not (ConnectionMode.FastbootBootloader or ConnectionMode.FastbootdUserspace))
        {
            blocked = true;
            safetyFindings.Add($"BLOCKED: Active connection mode '{device.ActiveConnectionMode}' does not have an unauthenticated/validated direct write provider. Connect in unlocked Fastboot or Fastbootd mode.");
        }

        var serial = device.TransportSerial ?? "UNAVAILABLE";
        var validatedRequests = new List<FlashPartitionRequest>();
        var steps = new List<FlashProposedStep>();
        bool touchesUserdata = false;
        int order = 1;

        steps.Add(new FlashProposedStep(
            order++,
            "precheck",
            "(device)",
            $"fastboot -s {serial} getvar unlocked",
            0,
            false,
            false,
            "Verify target serial is responsive and bootloader is unlocked."));

        // Step 4 & 5: Validate each selected firmware image, protected status, logical mode, and partition size
        foreach (var (rawPartName, imgPath) in requestedPartitions)
        {
            var partName = rawPartName.Trim();
            if (string.IsNullOrWhiteSpace(partName))
            {
                blocked = true;
                safetyFindings.Add("BLOCKED: Empty target partition name.");
                continue;
            }

            if (partName.Equals("userdata", StringComparison.OrdinalIgnoreCase))
            {
                touchesUserdata = true;
                safetyFindings.Add("WARNING: 'userdata' was explicitly included by user; TranssionLab never adds userdata erase automatically.");
            }

            // Protected NVRAM / Calibration guardrail
            bool isProtected = HashAndBinaryHelpers.IsProtectedPartition(partName) ||
                (profile?.ProtectedPartitions.Any(p => partName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ?? false);

            if (isProtected)
            {
                blocked = true;
                safetyFindings.Add($"BLOCKED: Partition '{partName}' is a protected radio/NVRAM/calibration/preloader partition and cannot be modified.");
            }

            // Logical partition mode guardrail (super sub-partitions require Fastbootd userspace!)
            bool isLogical = IsLogicalSuperPartition(partName, profile);
            if (isLogical && device.ActiveConnectionMode != ConnectionMode.FastbootdUserspace)
            {
                blocked = true;
                safetyFindings.Add($"BLOCKED: Partition '{partName}' is a logical dynamic partition inside 'super' and requires Userspace Fastbootd ('adb reboot fastboot' / 'fastboot reboot fastboot'), not Bootloader Fastboot.");
            }

            if (!File.Exists(imgPath))
            {
                blocked = true;
                safetyFindings.Add($"BLOCKED: Image file missing: '{imgPath}'.");
                continue;
            }

            var inspection = await _firmwareRegistry.InspectAsync(imgPath, ct).ConfigureAwait(false);
            if (!inspection.IsFormatSupported ||
                inspection.PackageFormat is not (
                    FirmwareFormatKind.AndroidSparseImage or
                    FirmwareFormatKind.AndroidSuperImage or
                    FirmwareFormatKind.AndroidBootImage or
                    FirmwareFormatKind.AndroidVendorBootImage or
                    FirmwareFormatKind.AndroidVbmetaImage or
                    FirmwareFormatKind.RawPartitionImage))
            {
                blocked = true;
                safetyFindings.Add($"BLOCKED: File '{inspection.FileName}' is format '{inspection.PackageFormat}', not a flashable partition image. Extract archives in Firmware Explorer / ROM Studio first.");
            }

            if (inspection.SparseInfo is { AccountingValid: false })
            {
                blocked = true;
                safetyFindings.Add($"BLOCKED: Sparse image '{inspection.FileName}' failed chunk integrity accounting.");
            }

            long effectiveBytes = inspection.Partitions.Count > 0 ? inspection.Partitions[0].EffectiveUnsparsedBytes : inspection.FileSizeBytes;

            // Check profile max expected bytes if known
            var profilePartRule = profile?.KnownPartitions.FirstOrDefault(kp =>
                string.Equals(kp.PartitionName, partName, StringComparison.OrdinalIgnoreCase));
            if (profilePartRule?.MaxExpectedBytes is long maxBytes && effectiveBytes > maxBytes)
            {
                blocked = true;
                safetyFindings.Add($"BLOCKED: Image '{inspection.FileName}' ({effectiveBytes:N0} B) exceeds profile capacity for '{partName}' ({maxBytes:N0} B).");
            }

            validatedRequests.Add(new FlashPartitionRequest(
                partName,
                Path.GetFullPath(imgPath),
                inspection.FileSizeBytes,
                effectiveBytes,
                inspection.Sha256Hex ?? "UNHASHED"));

            steps.Add(new FlashProposedStep(
                order++,
                "flash",
                partName,
                $"fastboot -s {serial} flash {partName} \"{Path.GetFileName(imgPath)}\"",
                effectiveBytes,
                true,
                isLogical,
                $"Format={inspection.PackageFormat}, SHA256={(inspection.Sha256Hex is { Length: >= 12 } h ? h[..12] : "N/A")}..."));
        }

        steps.Add(new FlashProposedStep(
            order,
            "verify",
            "(device)",
            $"fastboot -s {serial} getvar current-slot",
            0,
            false,
            false,
            "Verify device remains responsive in fastboot after partition writes."));

        var planId = $"PLAN-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var canonical = $"{planId}|{device.DeviceKey}|{serial}|{string.Join(';', validatedRequests.Select(r => $"{r.PartitionName}:{r.ImageSha256}:{r.EffectiveUnsparsedBytes}"))}";
        var planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var confirmPhrase = $"FLASH {serial}";

        _logger.LogInfo(
            "FlashManager",
            $"Built execution plan {planId} for {device.DeviceKey} ({validatedRequests.Count} partitions, Blocked={blocked})",
            string.Join(" | ", safetyFindings),
            device.DeviceKey,
            planId);

        return new FlashExecutionPlan(
            planId,
            device.DeviceKey,
            serial,
            profile?.ProfileId ?? "UNMATCHED",
            device.ActiveConnectionMode,
            validatedRequests,
            steps,
            touchesUserdata,
            blocked,
            safetyFindings,
            planHash,
            confirmPhrase,
            DateTimeOffset.UtcNow);
    }

    public async Task<DryRunValidationReport> RunDryRunValidationAsync(FlashExecutionPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var checks = new List<string>();
        var errors = new List<string>();

        if (plan.BlockedBySafetyPolicy)
        {
            errors.AddRange(plan.SafetyFindings.Where(f => f.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase)));
        }

        // 1. Check fastboot tool availability
        bool fbAvailable = await _fastboot.IsToolAvailableAsync(ct).ConfigureAwait(false);
        checks.Add($"Fastboot executable check: {(fbAvailable ? "AVAILABLE" : "MISSING")}");
        if (!fbAvailable)
            errors.Add("fastboot executable is not available.");

        // 2. Check target device still present in live fastboot list
        bool devicePresent = false;
        bool unlockedVerified = false;
        bool sizesCompatible = true;

        if (fbAvailable)
        {
            var endpoints = await _fastboot.ListDevicesAsync(ct).ConfigureAwait(false);
            var liveEp = endpoints.FirstOrDefault(e => string.Equals(e.Serial, plan.TransportSerial, StringComparison.OrdinalIgnoreCase));
            devicePresent = liveEp is not null;
            checks.Add($"Physical device '{plan.TransportSerial}' live presence: {(devicePresent ? $"CONNECTED ({liveEp!.Mode})" : "NOT FOUND")}");
            if (!devicePresent)
            {
                errors.Add($"Target device '{plan.TransportSerial}' is no longer connected in fastboot.");
            }
            else
            {
                var unlocked = await _fastboot.GetVariableAsync(plan.TransportSerial, "unlocked", ct).ConfigureAwait(false);
                unlockedVerified = string.Equals(unlocked, "yes", StringComparison.OrdinalIgnoreCase);
                checks.Add($"Bootloader unlock status: unlocked={unlocked ?? "unknown"}");
                if (!unlockedVerified)
                {
                    errors.Add("Target bootloader is NOT unlocked (unlocked != yes).");
                }

                // Query live partition-size:<name> where device reports it
                foreach (var req in plan.Partitions)
                {
                    var partSizeRaw = await _fastboot.GetVariableAsync(plan.TransportSerial, $"partition-size:{req.PartitionName}", ct).ConfigureAwait(false);
                    var reportedCap = TryParseFastbootSize(partSizeRaw);
                    if (reportedCap.HasValue && reportedCap.Value > 0)
                    {
                        if (req.EffectiveUnsparsedBytes > reportedCap.Value)
                        {
                            sizesCompatible = false;
                            errors.Add($"Image for '{req.PartitionName}' ({req.EffectiveUnsparsedBytes:N0} B) exceeds target partition-size ({reportedCap.Value:N0} B).");
                        }
                        else
                        {
                            checks.Add($"Partition '{req.PartitionName}' capacity verified: {req.EffectiveUnsparsedBytes:N0} B <= {reportedCap.Value:N0} B.");
                        }
                    }
                    else
                    {
                        checks.Add($"Partition '{req.PartitionName}' size variable not reported by bootloader; relying on profile rule.");
                    }
                }
            }
        }

        // 3. Re-verify image readability & SHA-256 unchanged since plan creation
        bool imagesValid = true;
        foreach (var req in plan.Partitions)
        {
            if (!File.Exists(req.ImageFilePath))
            {
                imagesValid = false;
                errors.Add($"Image file disappeared before dry-run: {req.ImageFilePath}");
                continue;
            }

            var (sha, _) = await HashAndBinaryHelpers.ComputeHashesAsync(req.ImageFilePath, ct: ct).ConfigureAwait(false);
            if (req.ImageSha256 != "UNHASHED" && !string.Equals(sha, req.ImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                imagesValid = false;
                errors.Add($"Image SHA-256 changed on disk since plan creation for '{req.PartitionName}'. Rebuild plan.");
            }
            else
            {
                checks.Add($"Image '{Path.GetFileName(req.ImageFilePath)}' readability & SHA-256 verified.");
            }
        }

        bool profileVerified = !string.Equals(plan.MatchedProfileId, "UNMATCHED", StringComparison.OrdinalIgnoreCase) ||
                               !_settings.Current.RequireStrictProfileMatchBeforeFlash;
        bool protectedUntouched = !plan.Partitions.Any(p => HashAndBinaryHelpers.IsProtectedPartition(p.PartitionName));

        bool passed = errors.Count == 0 && devicePresent && unlockedVerified && imagesValid && sizesCompatible && profileVerified && protectedUntouched;

        _logger.LogInfo(
            "FlashManager",
            $"Dry-run validation for {plan.PlanId} -> Passed={passed} ({errors.Count} blocking errors)",
            string.Join("; ", errors.Count > 0 ? errors : checks),
            plan.DeviceKey,
            plan.PlanId);

        return new DryRunValidationReport(
            plan.PlanId,
            passed,
            devicePresent,
            profileVerified,
            fbAvailable,
            unlockedVerified,
            imagesValid,
            sizesCompatible,
            protectedUntouched,
            checks,
            errors,
            DateTimeOffset.UtcNow);
    }

    public async Task<FlashOperationReport> ExecutePlanAsync(
        FlashExecutionPlan plan,
        DryRunValidationReport dryRunReport,
        FlashConfirmationToken confirmation,
        IProgress<(int StepIndex, int TotalSteps, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(dryRunReport);
        ArgumentNullException.ThrowIfNull(confirmation);

        if (plan.BlockedBySafetyPolicy || !dryRunReport.Passed || dryRunReport.PlanId != plan.PlanId)
        {
            throw new SafetyPolicyViolationException("Cannot execute flash plan: dry-run validation did not pass or does not match the active plan.");
        }

        if (confirmation.PlanId != plan.PlanId ||
            !string.Equals(confirmation.PlanHashSha256, plan.PlanHashSha256, StringComparison.OrdinalIgnoreCase) ||
            !confirmation.UserAcknowledgedDataAndBrickRisk ||
            !string.Equals(confirmation.EnteredConfirmationText?.Trim(), plan.RequiredConfirmationText, StringComparison.Ordinal))
        {
            throw new SafetyPolicyViolationException(
                $"Explicit confirmation invalid. Confirm risk checkbox and type '{plan.RequiredConfirmationText}' exactly.");
        }

        var opId = $"OP-FL-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var transcripts = new List<string>();
        var errors = new List<string>();
        bool allSucceeded = true;

        int totalSteps = plan.Partitions.Count + 2;
        int currentStep = 1;
        progress?.Report((currentStep, totalSteps, $"Verifying {plan.TransportSerial} before write..."));

        var preUnlock = await _fastboot.GetVariableAsync(plan.TransportSerial, "unlocked", ct).ConfigureAwait(false);
        if (!string.Equals(preUnlock, "yes", StringComparison.OrdinalIgnoreCase))
        {
            throw new SafetyPolicyViolationException("Pre-write check failed: target bootloader no longer reports unlocked=yes.");
        }
        transcripts.Add($"[Pre-Check] unlocked={preUnlock}");

        // Step 9: Execute permitted partition writes sequentially; check cancellation safely ONLY between partitions
        foreach (var part in plan.Partitions)
        {
            if (ct.IsCancellationRequested)
            {
                allSucceeded = false;
                errors.Add($"Operation safely cancelled before starting partition '{part.PartitionName}'.");
                break;
            }

            currentStep++;
            progress?.Report((currentStep, totalSteps, $"Flashing {part.PartitionName} ({Path.GetFileName(part.ImageFilePath)})..."));

            // Do not pass user UI cancellation mid-flash to avoid corrupting partition header; let process timeout guard hang
            var res = await _fastboot.FlashPartitionAsync(plan.TransportSerial, part.PartitionName, part.ImageFilePath, CancellationToken.None).ConfigureAwait(false);
            transcripts.Add($"[Flash {part.PartitionName}] Exit={res.ExitCode} ({res.DurationMs} ms)\n{res.StandardOutput}\n{res.StandardError}".Trim());

            if (!res.Succeeded)
            {
                allSucceeded = false;
                errors.Add($"Failed flashing '{part.PartitionName}': {res.StandardError}");
                break;
            }
        }

        // Step 10: Verify resulting device state
        currentStep = totalSteps;
        progress?.Report((currentStep, totalSteps, "Verifying post-flash device state..."));
        var postSlot = await _fastboot.GetVariableAsync(plan.TransportSerial, "current-slot", CancellationToken.None).ConfigureAwait(false);
        var postUnlocked = await _fastboot.GetVariableAsync(plan.TransportSerial, "unlocked", CancellationToken.None).ConfigureAwait(false);
        bool postVerified = allSucceeded && !string.IsNullOrWhiteSpace(postUnlocked);
        var verifySummary = postVerified
            ? $"Post-flash state verified on {plan.TransportSerial} (unlocked={postUnlocked}, current-slot={postSlot ?? "N/A"})."
            : $"Post-flash verification incomplete or write failed ({errors.FirstOrDefault() ?? "device did not respond to post-check"}).";

        sw.Stop();
        var completed = DateTimeOffset.UtcNow;

        // Step 11: Save persistent operation report
        var reportsDir = Path.Combine(_settings.Current.WorkspaceDataDirectory, "reports");
        Directory.CreateDirectory(reportsDir);
        var reportPath = Path.Combine(reportsDir, $"{opId}.json");

        var report = new FlashOperationReport(
            opId,
            plan.PlanId,
            _logger.CurrentSessionId,
            plan.DeviceKey,
            plan.TransportSerial,
            plan.MatchedProfileId,
            plan.ProtocolMode,
            DryRunOnly: false,
            Succeeded: allSucceeded,
            PostStateVerified: postVerified,
            VerificationSummary: verifySummary,
            StepTranscripts: transcripts,
            Errors: errors,
            StartedAtUtc: started,
            CompletedAtUtc: completed,
            SavedReportPath: reportPath);

        var json = JsonSerializer.Serialize(report, JsonOpts);
        await File.WriteAllTextAsync(reportPath, json, Encoding.UTF8, CancellationToken.None).ConfigureAwait(false);

        await _historyStore.RecordAsync(new OperationHistoryRecord(
            opId,
            _logger.CurrentSessionId,
            started,
            completed,
            sw.ElapsedMilliseconds,
            "Flash",
            $"Fastboot Flash ({plan.Partitions.Count} partition(s))",
            plan.DeviceKey,
            _fastboot.ProviderName,
            allSucceeded && postVerified,
            false,
            verifySummary,
            $"Flashed [{string.Join(", ", plan.Partitions.Select(p => p.PartitionName))}]",
            reportPath), CancellationToken.None).ConfigureAwait(false);

        return report;
    }

    private static bool IsLogicalSuperPartition(string partitionName, DeviceProfile? profile)
    {
        var rule = profile?.KnownPartitions.FirstOrDefault(p =>
            string.Equals(p.PartitionName, partitionName, StringComparison.OrdinalIgnoreCase));
        if (rule is not null)
            return rule.IsLogicalInSuper;

        var baseName = partitionName.Trim();
        if (baseName.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ||
            baseName.EndsWith("_b", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^2];
        }
        return KnownLogicalPartitionPrefixes.Contains(baseName);
    }

    private static long? TryParseFastbootSize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : null;
    }
}
