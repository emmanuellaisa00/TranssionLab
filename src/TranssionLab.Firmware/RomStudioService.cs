using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Firmware;

/// <summary>
/// ROM Studio service (Section 6).
/// Strictly separates Stock Firmware Extraction & Partition Preparation from
/// Source-Based Custom ROM Construction, validating real AOSP/LineageOS prerequisites
/// without ever claiming to build a custom ROM from a stock archive alone.
/// </summary>
public sealed class RomStudioService
{
    public const long MinimumSourceBuildFreeBytes = 350L * 1024 * 1024 * 1024; // 350 GiB

    private readonly IFirmwareFormatRegistry _firmwareRegistry;
    private readonly IStructuredLogger _logger;

    public RomStudioService(IFirmwareFormatRegistry firmwareRegistry, IStructuredLogger logger)
    {
        _firmwareRegistry = firmwareRegistry;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ExtractStockPartitionsForFastbootAsync(
        string stockPackagePath,
        IReadOnlyList<string> selectedEntryNames,
        string stagingOutputDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(stagingOutputDir);
        var extractedPaths = new List<string>();

        foreach (var entry in selectedEntryNames)
        {
            if (HashAndBinaryHelpers.IsProtectedPartition(Path.GetFileNameWithoutExtension(entry)))
            {
                _logger.LogWarning("ROMStudio", "SkipProtectedExtract", $"Skipped protected NVRAM/radio partition '{entry}'.");
                continue;
            }

            var outFile = await _firmwareRegistry.ExtractArchiveEntryAsync(stockPackagePath, entry, stagingOutputDir, ct).ConfigureAwait(false);
            extractedPaths.Add(outFile);
        }

        return extractedPaths;
    }

    public RomBuildEnvironmentReport ValidateCustomRomSourceWorkspace(string workspaceDirectory)
    {
        var findings = new List<string>();
        bool dirExists = !string.IsNullOrWhiteSpace(workspaceDirectory) && Directory.Exists(workspaceDirectory);

        bool isPosixHost = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
        if (!isPosixHost)
        {
            findings.Add("Native Windows NTFS hosts cannot compile AOSP directly; WSL2 (Ubuntu 22.04/24.04 on ext4 vhdx) or native Linux x64 host is mandatory.");
        }

        long freeBytes = 0;
        if (dirExists)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(workspaceDirectory));
                if (!string.IsNullOrWhiteSpace(root))
                {
                    freeBytes = new DriveInfo(root).AvailableFreeSpace;
                }
            }
            catch
            {
                // Ignore drive query failure on virtual paths
            }
        }

        bool hasDisk = freeBytes >= MinimumSourceBuildFreeBytes;
        if (!hasDisk)
        {
            findings.Add($"Insufficient free disk space ({freeBytes / (1024.0 * 1024 * 1024):F1} GiB available; >= 350 GiB required for Android 14–16 AOSP + ccache + out/).");
        }

        bool hasRepo = dirExists && Directory.Exists(Path.Combine(workspaceDirectory, ".repo"));
        bool hasDeviceTree = dirExists && (
            Directory.Exists(Path.Combine(workspaceDirectory, "device", "transsion")) ||
            Directory.Exists(Path.Combine(workspaceDirectory, "device", "tecno")) ||
            Directory.Exists(Path.Combine(workspaceDirectory, "device", "infinix")) ||
            Directory.Exists(Path.Combine(workspaceDirectory, "device", "itel")));
        bool hasKernel = dirExists && Directory.Exists(Path.Combine(workspaceDirectory, "kernel"));
        bool hasVendor = dirExists && Directory.Exists(Path.Combine(workspaceDirectory, "vendor"));

        if (!hasRepo) findings.Add("Missing '.repo' Android source manifest checkout.");
        if (!hasDeviceTree) findings.Add("Missing verified device tree under device/{transsion,tecno,infinix,itel}/<codename> (BoardConfig.mk, device.mk, SEPolicy).");
        if (!hasKernel) findings.Add("Missing kernel source tree or GKI 2.0 prebuilt/module build configuration under kernel/.");
        if (!hasVendor) findings.Add("Missing extracted proprietary vendor blobs under vendor/.");

        findings.Add("Policy Note: Stock firmware archives (.zip/.pac/scatter) can only yield partition images or vendor blobs—they CANNOT be converted into a source-built custom ROM without an AOSP tree, device tree, and kernel.");

        var checklist = new List<string>
        {
            "1. Linux x86_64 or WSL2 Ubuntu 22.04+ environment with >= 32 GB RAM + 32 GB swap",
            "2. >= 350 GiB free SSD storage for AOSP Android 14–16 source + build artifacts",
            "3. Initialized repo manifest (.repo/manifest.xml) for target Android branch",
            "4. Device-specific BoardConfig.mk, dynamic super partition sizing, and SELinux policy",
            "5. Matching GPL kernel source or Android 13–16 GKI 2.0 kernel + vendor_dlkm modules",
            "6. Verified vendor/ blobs matching exact Transsion firmware security patch level",
            "7. User-owned AVB / OTA release-key signing certificates (never commit private keys)"
        };

        bool ready = isPosixHost && hasDisk && hasRepo && hasDeviceTree && hasKernel && hasVendor;

        _logger.LogInfo(
            "ROMStudio",
            $"Validated custom ROM build workspace '{workspaceDirectory}' -> Ready={ready}");

        return new RomBuildEnvironmentReport(
            workspaceDirectory,
            Environment.OSVersion.ToString(),
            isPosixHost,
            freeBytes,
            hasDisk,
            hasRepo,
            hasDeviceTree,
            hasKernel,
            hasVendor,
            findings,
            checklist,
            ready);
    }
}
