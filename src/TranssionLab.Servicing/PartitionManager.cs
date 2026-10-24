using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Firmware;

namespace TranssionLab.Servicing;

/// <summary>
/// Capability-aware, safety-enforced Partition Manager for TECNO, Infinix, and itel devices.
/// Supports:
/// - Live GPT & Dynamic Super partition inspection across Fastboot, Fastbootd, ADB, and Recovery
/// - Dynamic logical partition lifecycle (create / resize / delete) in Userspace Fastbootd
/// - Controlled partition erase (blocking FRP, NVRAM/radio calibration, and critical bootloaders)
/// - Authorized unprotected firmware partition readback (.img + SHA-256) in Recovery / root ADB
/// - Verified AVB0 vbmeta flashing with optional verity/verification flags on unlocked devices
/// </summary>
public sealed class PartitionManager : IPartitionManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly HashSet<string> KnownLogicalPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "vendor", "product", "system_ext", "odm",
        "vendor_dlkm", "system_dlkm", "odm_dlkm",
        "tr_mi", "tr_theme", "tr_region", "tr_company", "tr_carrier", "tr_preload", "tr_product"
    };

    private static readonly HashSet<string> SafeErasableBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache", "metadata", "userdata"
    };

    private static readonly string[] FallbackFastbootProbePartitions =
    [
        "boot_a", "boot_b", "init_boot_a", "init_boot_b",
        "vendor_boot_a", "vendor_boot_b", "vbmeta_a", "vbmeta_b",
        "vbmeta_system_a", "dtbo_a", "recovery_a", "super",
        "system_a", "vendor_a", "product_a", "tr_mi_a",
        "userdata", "metadata", "nvram", "nvdata", "persist", "prodnv", "frp"
    ];

    private readonly IDeviceDiscoveryProvider _discovery;
    private readonly IDeviceProfileRepository _profileRepo;
    private readonly IFastbootProvider _fastboot;
    private readonly IAdbProvider _adb;
    private readonly IFirmwareFormatRegistry _firmwareRegistry;
    private readonly IStructuredLogger _logger;
    private readonly IOperationHistoryStore _historyStore;

    public PartitionManager(
        IDeviceDiscoveryProvider discovery,
        IDeviceProfileRepository profileRepo,
        IFastbootProvider fastboot,
        IAdbProvider adb,
        IFirmwareFormatRegistry firmwareRegistry,
        IStructuredLogger logger,
        IOperationHistoryStore historyStore)
    {
        _discovery = discovery;
        _profileRepo = profileRepo;
        _fastboot = fastboot;
        _adb = adb;
        _firmwareRegistry = firmwareRegistry;
        _logger = logger;
        _historyStore = historyStore;
    }

    public async Task<DevicePartitionTableSnapshot> ScanDevicePartitionsAsync(
        ConnectedDeviceSnapshot device,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (_discovery.RequiresExplicitSelection)
        {
            throw new AmbiguousDeviceSelectionException(_discovery.ConnectedDevices.Count);
        }

        var serial = device.TransportSerial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            throw new ServicingException(
                "PARTITION_SCAN_NO_SERIAL",
                $"Active connection mode '{device.ActiveConnectionMode}' does not expose an ADB or Fastboot transport serial for partition table enumeration.");
        }

        var profile = !string.IsNullOrWhiteSpace(device.MatchedProfileId)
            ? _profileRepo.GetById(device.MatchedProfileId)
            : null;

        if (device.ActiveConnectionMode is ConnectionMode.FastbootBootloader or ConnectionMode.FastbootdUserspace)
        {
            return await ScanFastbootPartitionsAsync(device, serial, profile, ct).ConfigureAwait(false);
        }

        if (device.ActiveConnectionMode is ConnectionMode.AdbAuthorized or ConnectionMode.Recovery)
        {
            return await ScanAdbPartitionsAsync(device, serial, profile, ct).ConfigureAwait(false);
        }

        throw new CapabilityUnavailableException(
            $"Live partition table enumeration is not supported in mode '{device.ActiveConnectionMode}'. Connect via Fastboot, Fastbootd, Authorized ADB, or Recovery.");
    }

    private async Task<DevicePartitionTableSnapshot> ScanFastbootPartitionsAsync(
        ConnectedDeviceSnapshot device,
        string serial,
        DeviceProfile? profile,
        CancellationToken ct)
    {
        var vars = new Dictionary<string, string>(
            await _fastboot.GetAllVariablesAsync(serial, ct).ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);

        vars.TryGetValue("current-slot", out var activeSlot);
        vars.TryGetValue("super-partition-name", out var superName);
        vars.TryGetValue("unlocked", out var unlockedStr);
        bool isUnlocked = string.Equals(unlockedStr, "yes", StringComparison.OrdinalIgnoreCase) ||
                          device.BootloaderState.Value == BootloaderLockState.Unlocked;
        bool isFastbootd = device.ActiveConnectionMode == ConnectionMode.FastbootdUserspace;

        var discoveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in vars.Keys)
        {
            if (k.StartsWith("partition-size:", StringComparison.OrdinalIgnoreCase))
                discoveredNames.Add(k["partition-size:".Length..]);
            else if (k.StartsWith("partition-type:", StringComparison.OrdinalIgnoreCase))
                discoveredNames.Add(k["partition-type:".Length..]);
            else if (k.StartsWith("is-logical:", StringComparison.OrdinalIgnoreCase))
                discoveredNames.Add(k["is-logical:".Length..]);
        }

        // Fallback targeted queries when MediaTek LK omits partition-size entries in 'getvar all'
        if (discoveredNames.Count == 0)
        {
            foreach (var cand in FallbackFastbootProbePartitions)
            {
                var sz = await _fastboot.GetVariableAsync(serial, $"partition-size:{cand}", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(sz))
                {
                    vars[$"partition-size:{cand}"] = sz;
                    discoveredNames.Add(cand);
                    var logVal = await _fastboot.GetVariableAsync(serial, $"is-logical:{cand}", ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(logVal))
                        vars[$"is-logical:{cand}"] = logVal;
                }
            }
        }

        var entries = new List<DevicePartitionEntry>();
        foreach (var partName in discoveredNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            vars.TryGetValue($"partition-size:{partName}", out var rawSize);
            vars.TryGetValue($"partition-type:{partName}", out var rawType);
            vars.TryGetValue($"is-logical:{partName}", out var rawLogical);

            var baseName = HashAndBinaryHelpers.StripSlotSuffix(partName);
            vars.TryGetValue($"has-slot:{baseName}", out var rawHasSlot);

            long? sizeBytes = TryParseFastbootSize(rawSize);
            var slotSuffix = partName.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ? "a"
                           : partName.EndsWith("_b", StringComparison.OrdinalIgnoreCase) ? "b"
                           : string.Empty;
            bool hasSlot = !string.IsNullOrEmpty(slotSuffix) || string.Equals(rawHasSlot, "yes", StringComparison.OrdinalIgnoreCase);

            bool isLogical = string.Equals(rawLogical, "yes", StringComparison.OrdinalIgnoreCase) ||
                             (rawLogical is null && IsKnownLogicalPartition(partName, profile));

            var (policy, notice) = ClassifyPartitionPolicy(partName, isLogical, profile);
            bool canFlash = isUnlocked && (
                policy == PartitionAccessPolicy.SafeFlashable ||
                (policy == PartitionAccessPolicy.LogicalSuperRequiresFastbootd && isFastbootd));
            bool canErase = isUnlocked && !isLogical && SafeErasableBaseNames.Contains(baseName) &&
                            policy is PartitionAccessPolicy.SafeFlashable or PartitionAccessPolicy.UserdataExplicitEraseOnly;
            bool canManageLogical = isUnlocked && isFastbootd && isLogical &&
                                    policy == PartitionAccessPolicy.LogicalSuperRequiresFastbootd;

            entries.Add(new DevicePartitionEntry(
                PartitionName: partName,
                BaseName: baseName,
                SlotSuffix: slotSuffix,
                SizeBytes: sizeBytes,
                FormattedSize: HashAndBinaryHelpers.FormatBytesHuman(sizeBytes),
                PartitionType: string.IsNullOrWhiteSpace(rawType) ? "raw" : rawType,
                IsLogicalInSuper: isLogical,
                HasSlot: hasSlot,
                DeviceBlockNode: null,
                AccessPolicy: policy,
                CanFlashInCurrentMode: canFlash,
                CanEraseInCurrentMode: canErase,
                CanReadbackInCurrentMode: false,
                CanResizeOrDeleteLogical: canManageLogical,
                Provenance: DataProvenance.Verified,
                SafetyNotice: notice));
        }

        bool usesDynamic = !string.IsNullOrWhiteSpace(superName) ||
                           entries.Any(e => e.IsLogicalInSuper || string.Equals(e.BaseName, "super", StringComparison.OrdinalIgnoreCase));

        return new DevicePartitionTableSnapshot(
            device.DeviceKey,
            serial,
            device.ActiveConnectionMode,
            activeSlot,
            superName ?? (usesDynamic ? "super" : null),
            usesDynamic,
            entries.Count,
            entries.Count(e => e.IsLogicalInSuper),
            entries.Count(e => e.AccessPolicy is PartitionAccessPolicy.ProtectedNvramOrCalibrationBlocked
                                              or PartitionAccessPolicy.BootChainCriticalBlocked
                                              or PartitionAccessPolicy.AntiTheftFrpBlocked),
            entries,
            DateTimeOffset.UtcNow);
    }

    private async Task<DevicePartitionTableSnapshot> ScanAdbPartitionsAsync(
        ConnectedDeviceSnapshot device,
        string serial,
        DeviceProfile? profile,
        CancellationToken ct)
    {
        var adbBlocks = await _adb.EnumerateBlockPartitionsAsync(serial, ct).ConfigureAwait(false);
        device.RawProperties.TryGetValue("ro.boot.slot_suffix", out var slotProp);
        device.RawProperties.TryGetValue("ro.boot.dynamic_partitions", out var dynProp);
        device.RawProperties.TryGetValue("ro.boot.super_partition", out var superProp);

        var activeSlot = slotProp?.TrimStart('_');
        var entries = new List<DevicePartitionEntry>();

        foreach (var blk in adbBlocks)
        {
            var baseName = HashAndBinaryHelpers.StripSlotSuffix(blk.PartitionName);
            var slotSuffix = blk.PartitionName.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ? "a"
                           : blk.PartitionName.EndsWith("_b", StringComparison.OrdinalIgnoreCase) ? "b"
                           : string.Empty;
            bool isLogical = blk.ResolvedBlockNode.Contains("/dm-", StringComparison.OrdinalIgnoreCase) ||
                             IsKnownLogicalPartition(blk.PartitionName, profile);

            var (policy, notice) = ClassifyPartitionPolicy(blk.PartitionName, isLogical, profile);
            bool canReadback = blk.IsReadableByCurrentShell &&
                               policy is PartitionAccessPolicy.SafeFlashable or PartitionAccessPolicy.LogicalSuperRequiresFastbootd;

            entries.Add(new DevicePartitionEntry(
                PartitionName: blk.PartitionName,
                BaseName: baseName,
                SlotSuffix: slotSuffix,
                SizeBytes: blk.SizeBytes,
                FormattedSize: HashAndBinaryHelpers.FormatBytesHuman(blk.SizeBytes),
                PartitionType: isLogical ? "dm-linear" : "block",
                IsLogicalInSuper: isLogical,
                HasSlot: !string.IsNullOrEmpty(slotSuffix),
                DeviceBlockNode: blk.ResolvedBlockNode,
                AccessPolicy: policy,
                CanFlashInCurrentMode: false,
                CanEraseInCurrentMode: false,
                CanReadbackInCurrentMode: canReadback,
                CanResizeOrDeleteLogical: false,
                Provenance: DataProvenance.Detected,
                SafetyNotice: blk.IsReadableByCurrentShell
                    ? notice
                    : $"{notice} (ADB non-root shell: raw block readback restricted by SELinux)"));
        }

        bool usesDynamic = string.Equals(dynProp, "true", StringComparison.OrdinalIgnoreCase) ||
                           entries.Any(e => e.IsLogicalInSuper || string.Equals(e.BaseName, "super", StringComparison.OrdinalIgnoreCase));

        return new DevicePartitionTableSnapshot(
            device.DeviceKey,
            serial,
            device.ActiveConnectionMode,
            activeSlot,
            string.IsNullOrWhiteSpace(superProp) ? (usesDynamic ? "super" : null) : superProp,
            usesDynamic,
            entries.Count,
            entries.Count(e => e.IsLogicalInSuper),
            entries.Count(e => e.AccessPolicy is PartitionAccessPolicy.ProtectedNvramOrCalibrationBlocked
                                              or PartitionAccessPolicy.BootChainCriticalBlocked
                                              or PartitionAccessPolicy.AntiTheftFrpBlocked),
            entries,
            DateTimeOffset.UtcNow);
    }

    public async Task<DevicePartitionTableSnapshot> LoadOfflinePartitionTableAsync(
        string firmwareOrContainerPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmwareOrContainerPath);
        if (!File.Exists(firmwareOrContainerPath))
        {
            throw new FileNotFoundException("Firmware container or profile file was not found.", firmwareOrContainerPath);
        }

        var inspect = await _firmwareRegistry.InspectAsync(firmwareOrContainerPath, ct).ConfigureAwait(false);
        var entries = new List<DevicePartitionEntry>();

        if (inspect.SuperInfo is not null && inspect.SuperInfo.LogicalPartitions.Count > 0)
        {
            foreach (var lp in inspect.SuperInfo.LogicalPartitions)
            {
                var baseName = HashAndBinaryHelpers.StripSlotSuffix(lp.PartitionName);
                var slotSuffix = lp.PartitionName.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ? "a"
                               : lp.PartitionName.EndsWith("_b", StringComparison.OrdinalIgnoreCase) ? "b"
                               : string.Empty;
                var (policy, notice) = ClassifyPartitionPolicy(lp.PartitionName, isLogical: true, profile: null);
                entries.Add(new DevicePartitionEntry(
                    PartitionName: lp.PartitionName,
                    BaseName: baseName,
                    SlotSuffix: slotSuffix,
                    SizeBytes: lp.SizeBytes,
                    FormattedSize: HashAndBinaryHelpers.FormatBytesHuman(lp.SizeBytes),
                    PartitionType: $"lp:{lp.GroupName}",
                    IsLogicalInSuper: true,
                    HasSlot: !string.IsNullOrEmpty(slotSuffix),
                    DeviceBlockNode: inspect.FileName,
                    AccessPolicy: policy,
                    CanFlashInCurrentMode: false,
                    CanEraseInCurrentMode: false,
                    CanReadbackInCurrentMode: false,
                    CanResizeOrDeleteLogical: false,
                    Provenance: DataProvenance.Detected,
                    SafetyNotice: $"{notice} (Offline super.img LpMetadata layout)"));
            }
        }
        else
        {
            foreach (var p in inspect.Partitions)
            {
                var baseName = HashAndBinaryHelpers.StripSlotSuffix(p.PartitionName);
                var slotSuffix = p.PartitionName.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ? "a"
                               : p.PartitionName.EndsWith("_b", StringComparison.OrdinalIgnoreCase) ? "b"
                               : string.Empty;
                bool isLogical = IsKnownLogicalPartition(p.PartitionName, profile: null);
                var (policy, notice) = ClassifyPartitionPolicy(p.PartitionName, isLogical, profile: null);
                long? resolvedSize = p.DeclaredPartitionCapacityBytes is > 0
                    ? p.DeclaredPartitionCapacityBytes
                    : p.EffectiveUnsparsedBytes > 0 ? p.EffectiveUnsparsedBytes : null;

                entries.Add(new DevicePartitionEntry(
                    PartitionName: p.PartitionName,
                    BaseName: baseName,
                    SlotSuffix: slotSuffix,
                    SizeBytes: resolvedSize,
                    FormattedSize: resolvedSize.HasValue ? HashAndBinaryHelpers.FormatBytesHuman(resolvedSize) : "Unknown",
                    PartitionType: inspect.PackageFormat.ToString(),
                    IsLogicalInSuper: isLogical,
                    HasSlot: !string.IsNullOrEmpty(slotSuffix),
                    DeviceBlockNode: p.FileName,
                    AccessPolicy: policy,
                    CanFlashInCurrentMode: false,
                    CanEraseInCurrentMode: false,
                    CanReadbackInCurrentMode: false,
                    CanResizeOrDeleteLogical: false,
                    Provenance: DataProvenance.Detected,
                    SafetyNotice: $"{notice} (Offline {inspect.PackageFormat} layout)"));
            }
        }

        bool usesDynamic = entries.Any(e => e.IsLogicalInSuper || string.Equals(e.BaseName, "super", StringComparison.OrdinalIgnoreCase));
        var label = HashAndBinaryHelpers.SanitizeFileNameComponent(Path.GetFileNameWithoutExtension(inspect.FileName), "offline");

        _logger.LogInfo(
            "PartitionManager",
            $"Loaded offline partition layout ({entries.Count} partitions) from '{inspect.FileName}'",
            $"Format={inspect.PackageFormat}",
            $"OFFLINE:{label}",
            "partition.inspect_table");

        return new DevicePartitionTableSnapshot(
            $"OFFLINE:{inspect.FileName}",
            label,
            ConnectionMode.Unknown,
            null,
            usesDynamic ? "super" : null,
            usesDynamic,
            entries.Count,
            entries.Count(e => e.IsLogicalInSuper),
            entries.Count(e => e.AccessPolicy is PartitionAccessPolicy.ProtectedNvramOrCalibrationBlocked
                                              or PartitionAccessPolicy.BootChainCriticalBlocked
                                              or PartitionAccessPolicy.AntiTheftFrpBlocked),
            entries,
            DateTimeOffset.UtcNow);
    }

    public async Task<string> ExportPartitionTableJsonAsync(
        DevicePartitionTableSnapshot snapshot,
        string outputFilePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);

        var fullPath = Path.GetFullPath(outputFilePath);
        HashAndBinaryHelpers.EnsureParentDirectoryExists(fullPath);
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, ct).ConfigureAwait(false);
        _logger.LogInfo("PartitionManager", $"Exported partition table ({snapshot.TotalPartitions} partitions) -> '{fullPath}'", null, snapshot.DeviceKey, "partition.inspect_table");
        return fullPath;
    }

    public async Task<PartitionModificationResult> CreateLogicalPartitionAsync(
        ConnectedDeviceSnapshot device,
        string partitionName,
        long sizeBytes,
        string confirmationText,
        CancellationToken ct = default)
    {
        var serial = ValidateFastbootdLogicalMutationPrerequisites(device, partitionName, confirmationText);
        if (sizeBytes < 4096)
        {
            throw new ServicingException("INVALID_LOGICAL_SIZE", "Logical partition size must be at least 4096 bytes.");
        }

        var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var res = await _fastboot.CreateLogicalPartitionAsync(serial, partitionName.Trim(), sizeBytes, ct).ConfigureAwait(false);

        var checkLogical = await _fastboot.GetVariableAsync(serial, $"is-logical:{partitionName.Trim()}", ct).ConfigureAwait(false);
        bool postVerified = res.Succeeded && string.Equals(checkLogical, "yes", StringComparison.OrdinalIgnoreCase);
        sw.Stop();

        return await RecordAndBuildResultAsync(
            device.DeviceKey,
            "create-logical",
            partitionName.Trim(),
            res,
            postVerified,
            postVerified
                ? $"Created & verified logical partition '{partitionName.Trim()}' ({HashAndBinaryHelpers.FormatBytesHuman(sizeBytes)}) in super."
                : $"Create logical partition failed or could not be verified: {res.StandardError}",
            started,
            sw.ElapsedMilliseconds).ConfigureAwait(false);
    }

    public async Task<PartitionModificationResult> ResizeLogicalPartitionAsync(
        ConnectedDeviceSnapshot device,
        string partitionName,
        long newSizeBytes,
        string confirmationText,
        CancellationToken ct = default)
    {
        var serial = ValidateFastbootdLogicalMutationPrerequisites(device, partitionName, confirmationText);
        if (newSizeBytes < 4096)
        {
            throw new ServicingException("INVALID_LOGICAL_SIZE", "New logical partition size must be at least 4096 bytes.");
        }

        var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var res = await _fastboot.ResizeLogicalPartitionAsync(serial, partitionName.Trim(), newSizeBytes, ct).ConfigureAwait(false);

        var checkSizeRaw = await _fastboot.GetVariableAsync(serial, $"partition-size:{partitionName.Trim()}", ct).ConfigureAwait(false);
        var reportedBytes = TryParseFastbootSize(checkSizeRaw);
        bool postVerified = res.Succeeded && (!reportedBytes.HasValue || reportedBytes.Value == newSizeBytes);
        sw.Stop();

        return await RecordAndBuildResultAsync(
            device.DeviceKey,
            "resize-logical",
            partitionName.Trim(),
            res,
            postVerified,
            postVerified
                ? $"Resized & verified logical partition '{partitionName.Trim()}' -> {HashAndBinaryHelpers.FormatBytesHuman(newSizeBytes)}."
                : $"Resize logical partition failed or reported size mismatch: {res.StandardError}",
            started,
            sw.ElapsedMilliseconds).ConfigureAwait(false);
    }

    public async Task<PartitionModificationResult> DeleteLogicalPartitionAsync(
        ConnectedDeviceSnapshot device,
        string partitionName,
        string confirmationText,
        CancellationToken ct = default)
    {
        var serial = ValidateFastbootdLogicalMutationPrerequisites(device, partitionName, confirmationText);

        var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var res = await _fastboot.DeleteLogicalPartitionAsync(serial, partitionName.Trim(), ct).ConfigureAwait(false);

        var checkLogical = await _fastboot.GetVariableAsync(serial, $"is-logical:{partitionName.Trim()}", ct).ConfigureAwait(false);
        bool postVerified = res.Succeeded && !string.Equals(checkLogical, "yes", StringComparison.OrdinalIgnoreCase);
        sw.Stop();

        return await RecordAndBuildResultAsync(
            device.DeviceKey,
            "delete-logical",
            partitionName.Trim(),
            res,
            postVerified,
            postVerified
                ? $"Deleted logical partition '{partitionName.Trim()}' from super metadata."
                : $"Delete logical partition failed: {res.StandardError}",
            started,
            sw.ElapsedMilliseconds).ConfigureAwait(false);
    }

    public async Task<PartitionModificationResult> ErasePartitionAsync(
        ConnectedDeviceSnapshot device,
        string partitionName,
        string confirmationText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);

        if (_discovery.RequiresExplicitSelection)
        {
            throw new AmbiguousDeviceSelectionException(_discovery.ConnectedDevices.Count);
        }

        var trimmed = partitionName.Trim();
        var baseName = HashAndBinaryHelpers.StripSlotSuffix(trimmed);

        if (HashAndBinaryHelpers.IsAntiTheftFrpPartition(trimmed))
        {
            throw new SafetyPolicyViolationException(
                $"BLOCKED: Erasing anti-theft / Factory Reset Protection partition '{trimmed}' is permanently prohibited.");
        }

        if (HashAndBinaryHelpers.IsProtectedPartition(trimmed) || HashAndBinaryHelpers.IsBootChainCriticalPartition(trimmed))
        {
            throw new SafetyPolicyViolationException(
                $"BLOCKED: Partition '{trimmed}' is a protected NVRAM/radio calibration or critical boot-chain partition and cannot be erased.");
        }

        if (!SafeErasableBaseNames.Contains(baseName))
        {
            throw new SafetyPolicyViolationException(
                $"BLOCKED: Direct erase of '{trimmed}' is restricted to prevent unbootable states. Only [{string.Join(", ", SafeErasableBaseNames)}] may be erased directly; replace OS/boot partitions via validated flashing instead.");
        }

        if (device.ActiveConnectionMode is not (ConnectionMode.FastbootBootloader or ConnectionMode.FastbootdUserspace) ||
            string.IsNullOrWhiteSpace(device.TransportSerial))
        {
            throw new CapabilityUnavailableException("Controlled partition erase requires an active Fastboot or Fastbootd connection.");
        }

        var serial = device.TransportSerial;
        var expectedPhrase = $"ERASE {trimmed} {serial}";
        if (!string.Equals(confirmationText?.Trim(), expectedPhrase, StringComparison.Ordinal))
        {
            throw new SafetyPolicyViolationException($"Explicit confirmation mismatch. Type '{expectedPhrase}' exactly to erase '{trimmed}'.");
        }

        var liveUnlocked = await _fastboot.GetVariableAsync(serial, "unlocked", ct).ConfigureAwait(false);
        if (!string.Equals(liveUnlocked, "yes", StringComparison.OrdinalIgnoreCase))
        {
            throw new SafetyPolicyViolationException("Erase blocked: target bootloader does not report unlocked=yes.");
        }

        var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var res = await _fastboot.ErasePartitionAsync(serial, trimmed, ct).ConfigureAwait(false);
        var postUnlocked = await _fastboot.GetVariableAsync(serial, "unlocked", ct).ConfigureAwait(false);
        bool postVerified = res.Succeeded && !string.IsNullOrWhiteSpace(postUnlocked);
        sw.Stop();

        return await RecordAndBuildResultAsync(
            device.DeviceKey,
            "erase",
            trimmed,
            res,
            postVerified,
            postVerified
                ? $"Erased '{trimmed}' on {serial} and verified fastboot responsiveness."
                : $"Erase '{trimmed}' failed: {res.StandardError}",
            started,
            sw.ElapsedMilliseconds).ConfigureAwait(false);
    }

    public async Task<PartitionModificationResult> BackupPartitionViaAdbAsync(
        ConnectedDeviceSnapshot device,
        DevicePartitionEntry partition,
        string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (_discovery.RequiresExplicitSelection)
        {
            throw new AmbiguousDeviceSelectionException(_discovery.ConnectedDevices.Count);
        }

        if (HashAndBinaryHelpers.IsAntiTheftFrpPartition(partition.PartitionName) ||
            string.Equals(partition.BaseName, "userdata", StringComparison.OrdinalIgnoreCase) ||
            HashAndBinaryHelpers.IsProtectedPartition(partition.PartitionName))
        {
            throw new SafetyPolicyViolationException(
                $"BLOCKED: Partition '{partition.PartitionName}' contains user credentials, anti-theft state, or protected radio data and cannot be read back.");
        }

        if (device.ActiveConnectionMode is not (ConnectionMode.AdbAuthorized or ConnectionMode.Recovery) ||
            string.IsNullOrWhiteSpace(device.TransportSerial))
        {
            throw new CapabilityUnavailableException("Partition readback requires Authorized ADB or Recovery mode.");
        }

        var blockPath = !string.IsNullOrWhiteSpace(partition.DeviceBlockNode)
            ? partition.DeviceBlockNode
            : $"/dev/block/by-name/{partition.PartitionName}";

        Directory.CreateDirectory(outputDirectory);
        var safePart = HashAndBinaryHelpers.SanitizeFileNameComponent(partition.PartitionName, "partition");
        var safeSerial = HashAndBinaryHelpers.SanitizeFileNameComponent(device.TransportSerial, "device");
        var fileName = $"{safePart}_{safeSerial}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.img";
        var destPath = Path.GetFullPath(Path.Combine(outputDirectory, fileName));

        var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var (res, bytesRead, shaHex) = await _adb.ReadbackPartitionToFileAsync(
            device.TransportSerial,
            blockPath,
            destPath,
            ct).ConfigureAwait(false);
        sw.Stop();

        var opId = $"OP-PM-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        var summary = res.Succeeded
            ? $"Backed up '{partition.PartitionName}' ({HashAndBinaryHelpers.FormatBytesHuman(bytesRead)}) -> '{destPath}' [SHA256: {shaHex?[..16] ?? "SKIPPED"}...]"
            : $"Readback of '{partition.PartitionName}' failed or was denied by SELinux ({res.StandardError.Trim()}). Boot into Recovery or use an authorized root shell.";

        await _historyStore.RecordAsync(new OperationHistoryRecord(
            opId,
            _logger.CurrentSessionId,
            started,
            DateTimeOffset.UtcNow,
            sw.ElapsedMilliseconds,
            "Partition",
            $"ADB Partition Readback ({partition.PartitionName})",
            device.DeviceKey,
            _adb.ProviderName,
            res.Succeeded,
            res.Succeeded,
            summary,
            res.Succeeded ? $"Saved {bytesRead:N0} B" : "SELinux / pull error",
            res.Succeeded ? destPath : null), CancellationToken.None).ConfigureAwait(false);

        return new PartitionModificationResult(
            opId,
            "adb-readback",
            partition.PartitionName,
            res.Succeeded,
            res.Succeeded,
            summary,
            $"{res.StandardOutput}\n{res.StandardError}".Trim(),
            res.Succeeded ? destPath : null,
            shaHex,
            DateTimeOffset.UtcNow);
    }

    public async Task<PartitionModificationResult> FlashVbmetaWithPolicyAsync(
        ConnectedDeviceSnapshot device,
        string partitionName,
        string vbmetaImagePath,
        bool disableVerity,
        bool disableVerification,
        string confirmationText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(vbmetaImagePath);

        if (_discovery.RequiresExplicitSelection)
        {
            throw new AmbiguousDeviceSelectionException(_discovery.ConnectedDevices.Count);
        }

        var trimmed = partitionName.Trim();
        if (!trimmed.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase))
        {
            throw new SafetyPolicyViolationException("FlashVbmetaWithPolicyAsync is restricted to 'vbmeta*' partitions.");
        }

        if (device.ActiveConnectionMode is not (ConnectionMode.FastbootBootloader or ConnectionMode.FastbootdUserspace) ||
            string.IsNullOrWhiteSpace(device.TransportSerial))
        {
            throw new CapabilityUnavailableException("Flashing vbmeta requires an active Fastboot or Fastbootd connection.");
        }

        var serial = device.TransportSerial;
        var expectedPhrase = $"VBMETA {serial}";
        if (!string.Equals(confirmationText?.Trim(), expectedPhrase, StringComparison.Ordinal))
        {
            throw new SafetyPolicyViolationException($"Explicit confirmation mismatch. Type '{expectedPhrase}' exactly.");
        }

        var inspection = await _firmwareRegistry.InspectAsync(vbmetaImagePath, ct).ConfigureAwait(false);
        if (inspection.PackageFormat != FirmwareFormatKind.AndroidVbmetaImage)
        {
            throw new SafetyPolicyViolationException(
                $"Selected file '{inspection.FileName}' is '{inspection.PackageFormat}', not a valid AVB0 AndroidVbmetaImage.");
        }

        var liveUnlocked = await _fastboot.GetVariableAsync(serial, "unlocked", ct).ConfigureAwait(false);
        if (!string.Equals(liveUnlocked, "yes", StringComparison.OrdinalIgnoreCase))
        {
            throw new SafetyPolicyViolationException("Flashing vbmeta requires an already-unlocked bootloader (unlocked=yes).");
        }

        var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var res = await _fastboot.FlashVbmetaAsync(serial, trimmed, vbmetaImagePath, disableVerity, disableVerification, ct).ConfigureAwait(false);
        var postCheck = await _fastboot.GetVariableAsync(serial, "unlocked", ct).ConfigureAwait(false);
        bool postVerified = res.Succeeded && !string.IsNullOrWhiteSpace(postCheck);
        sw.Stop();

        return await RecordAndBuildResultAsync(
            device.DeviceKey,
            "vbmeta-flash",
            trimmed,
            res,
            postVerified,
            postVerified
                ? $"Flashed AVB0 '{trimmed}' (disableVerity={disableVerity}, disableVerification={disableVerification}) and verified target state."
                : $"Flashing '{trimmed}' failed: {res.StandardError}",
            started,
            sw.ElapsedMilliseconds).ConfigureAwait(false);
    }

    private string ValidateFastbootdLogicalMutationPrerequisites(
        ConnectedDeviceSnapshot device,
        string partitionName,
        string confirmationText)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);

        if (_discovery.RequiresExplicitSelection)
        {
            throw new AmbiguousDeviceSelectionException(_discovery.ConnectedDevices.Count);
        }

        if (device.ActiveConnectionMode != ConnectionMode.FastbootdUserspace || string.IsNullOrWhiteSpace(device.TransportSerial))
        {
            throw new CapabilityUnavailableException(
                "Creating, resizing, or deleting logical partitions inside 'super' strictly requires Userspace Fastbootd ('adb reboot fastboot' / 'fastboot reboot fastboot').");
        }

        if (device.BootloaderState.Value != BootloaderLockState.Unlocked)
        {
            throw new SafetyPolicyViolationException("Dynamic super partition modification requires an unlocked bootloader.");
        }

        var trimmed = partitionName.Trim();
        if (HashAndBinaryHelpers.IsProtectedPartition(trimmed) ||
            HashAndBinaryHelpers.IsBootChainCriticalPartition(trimmed) ||
            HashAndBinaryHelpers.IsAntiTheftFrpPartition(trimmed) ||
            string.Equals(trimmed, "super", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(HashAndBinaryHelpers.StripSlotSuffix(trimmed), "userdata", StringComparison.OrdinalIgnoreCase))
        {
            throw new SafetyPolicyViolationException($"Partition '{trimmed}' cannot be modified as a logical super sub-partition.");
        }

        var expectedPhrase = $"SUPER {device.TransportSerial}";
        if (!string.Equals(confirmationText?.Trim(), expectedPhrase, StringComparison.Ordinal))
        {
            throw new SafetyPolicyViolationException($"Explicit confirmation mismatch. Type '{expectedPhrase}' exactly.");
        }

        return device.TransportSerial;
    }

    private async Task<PartitionModificationResult> RecordAndBuildResultAsync(
        string deviceKey,
        string actionType,
        string partitionName,
        CommandExecutionResult res,
        bool postVerified,
        string summary,
        DateTimeOffset started,
        long durationMs)
    {
        var opId = $"OP-PM-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        await _historyStore.RecordAsync(new OperationHistoryRecord(
            opId,
            _logger.CurrentSessionId,
            started,
            DateTimeOffset.UtcNow,
            durationMs,
            "Partition",
            $"Partition {actionType} ({partitionName})",
            deviceKey,
            _fastboot.ProviderName,
            res.Succeeded && postVerified,
            postVerified,
            summary,
            $"Action={actionType}, Partition={partitionName}",
            null), CancellationToken.None).ConfigureAwait(false);

        return new PartitionModificationResult(
            opId,
            actionType,
            partitionName,
            res.Succeeded,
            postVerified,
            summary,
            $"{res.StandardOutput}\n{res.StandardError}".Trim(),
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private static (PartitionAccessPolicy Policy, string Notice) ClassifyPartitionPolicy(
        string partitionName,
        bool isLogical,
        DeviceProfile? profile)
    {
        var baseName = HashAndBinaryHelpers.StripSlotSuffix(partitionName);

        if (HashAndBinaryHelpers.IsAntiTheftFrpPartition(partitionName))
        {
            return (PartitionAccessPolicy.AntiTheftFrpBlocked, "Anti-theft / FRP partition — permanently blocked by compliance policy.");
        }

        if (HashAndBinaryHelpers.IsBootChainCriticalPartition(partitionName))
        {
            return (PartitionAccessPolicy.BootChainCriticalBlocked, "Critical primary boot-chain partition — unverified fastboot erase/overwrite blocked.");
        }

        bool profileProtected = profile?.ProtectedPartitions.Any(p =>
            string.Equals(p, partitionName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p, baseName, StringComparison.OrdinalIgnoreCase)) ?? false;

        if (profileProtected || HashAndBinaryHelpers.IsProtectedPartition(partitionName))
        {
            return (PartitionAccessPolicy.ProtectedNvramOrCalibrationBlocked, "Protected NVRAM / RF calibration / security partition — read/write/erase blocked.");
        }

        if (string.Equals(baseName, "userdata", StringComparison.OrdinalIgnoreCase))
        {
            return (PartitionAccessPolicy.UserdataExplicitEraseOnly, "User data partition — never auto-wiped; explicit confirmation required.");
        }

        if (isLogical)
        {
            return (PartitionAccessPolicy.LogicalSuperRequiresFastbootd, "Dynamic logical sub-partition inside 'super' — requires Userspace Fastbootd.");
        }

        return (PartitionAccessPolicy.SafeFlashable, "Standard partition — eligible for validated workflows when unlocked.");
    }

    private static bool IsKnownLogicalPartition(string partitionName, DeviceProfile? profile)
    {
        var rule = profile?.KnownPartitions.FirstOrDefault(p =>
            string.Equals(p.PartitionName, partitionName, StringComparison.OrdinalIgnoreCase));
        if (rule is not null)
            return rule.IsLogicalInSuper;

        var baseName = HashAndBinaryHelpers.StripSlotSuffix(partitionName);
        return KnownLogicalPrefixes.Contains(baseName);
    }

    private static long? TryParseFastbootSize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : null;
    }
}
