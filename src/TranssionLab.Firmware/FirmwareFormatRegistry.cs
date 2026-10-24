using TranssionLab.Core.Enums;
using TranssionLab.Core.Exceptions;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;
using TranssionLab.Firmware.Parsers;

namespace TranssionLab.Firmware;

/// <summary>
/// Format registry and local inspection engine for Firmware Explorer (Section 6).
/// Accurately distinguishes supported formats from unsupported proprietary containers.
/// </summary>
public sealed class FirmwareFormatRegistry : IFirmwareFormatRegistry
{
    private readonly IDeviceProfileRepository _profileRepo;
    private readonly IStructuredLogger _logger;

    public IReadOnlyList<FirmwareFormatKind> SupportedFormats { get; } =
    [
        FirmwareFormatKind.ZipArchive,
        FirmwareFormatKind.AndroidOtaZip,
        FirmwareFormatKind.TarArchive,
        FirmwareFormatKind.TarGzipArchive,
        FirmwareFormatKind.AndroidPayloadBin,
        FirmwareFormatKind.MediaTekScatterText,
        FirmwareFormatKind.MediaTekScatterXml,
        FirmwareFormatKind.UnisocPacContainer,
        FirmwareFormatKind.AndroidSparseImage,
        FirmwareFormatKind.AndroidBootImage,
        FirmwareFormatKind.AndroidVendorBootImage,
        FirmwareFormatKind.AndroidVbmetaImage,
        FirmwareFormatKind.RawPartitionImage
    ];

    public FirmwareFormatRegistry(IDeviceProfileRepository profileRepo, IStructuredLogger logger)
    {
        _profileRepo = profileRepo;
        _logger = logger;
    }

    public FirmwareFormatKind DetectFormat(string filePath)
    {
        if (!File.Exists(filePath))
            return FirmwareFormatKind.Unsupported;

        using var fs = File.OpenRead(filePath);
        return HashAndBinaryHelpers.SniffStreamMagic(fs, Path.GetFileName(filePath)).Kind;
    }

    public async Task<FirmwareInspectionResult> InspectAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            throw new ServicingException("FIRMWARE_NOT_FOUND", $"Firmware file does not exist: {filePath}");
        }

        var fi = new FileInfo(filePath);
        FirmwareFormatKind kind;
        string formatSummary;
        using (var fs = File.OpenRead(filePath))
        {
            (kind, formatSummary) = HashAndBinaryHelpers.SniffStreamMagic(fs, fi.Name);
        }

        var (sha256, crc32) = await HashAndBinaryHelpers.ComputeHashesAsync(filePath, ct: ct).ConfigureAwait(false);
        var warnings = new List<string>();
        var missingDeps = new List<string>();
        var archiveEntries = new List<FirmwareArchiveEntry>();
        var partitions = new List<PartitionImageMetadata>();

        SparseImageHeaderInfo? sparseInfo = null;
        BootImageHeaderInfo? bootInfo = null;
        PayloadManifestInfo? payloadInfo = null;
        UnisocPacHeaderInfo? pacInfo = null;
        MediaTekScatterInfo? scatterInfo = null;
        string? chipsetHint = null;
        string? modelHint = null;
        string? fpHint = null;

        switch (kind)
        {
            case FirmwareFormatKind.ZipArchive:
            {
                var (specificKind, zEntries, zParts, embPayload, devHint, postBuild, zWarn) =
                    TarAndZipArchiveInspector.InspectZip(filePath);
                kind = specificKind;
                archiveEntries.AddRange(zEntries);
                partitions.AddRange(zParts);
                payloadInfo = embPayload;
                modelHint = devHint;
                fpHint = postBuild;
                warnings.AddRange(zWarn);
                break;
            }

            case FirmwareFormatKind.TarArchive:
            case FirmwareFormatKind.TarGzipArchive:
            {
                var (tEntries, tParts, tWarn) = TarAndZipArchiveInspector.InspectTarOrTarGz(
                    filePath,
                    isGzip: kind == FirmwareFormatKind.TarGzipArchive);
                archiveEntries.AddRange(tEntries);
                partitions.AddRange(tParts);
                warnings.AddRange(tWarn);
                break;
            }

            case FirmwareFormatKind.AndroidPayloadBin:
            {
                using var fs = File.OpenRead(filePath);
                payloadInfo = AndroidPayloadBinParser.TryParsePayloadHeaderAndManifest(fs);
                if (payloadInfo is null)
                {
                    warnings.Add("CrAU header detected, but DeltaArchiveManifest could not be decoded or exceeds inspection bounds.");
                }
                else
                {
                    foreach (var p in payloadInfo.Partitions)
                    {
                        partitions.Add(new PartitionImageMetadata(
                            p.PartitionName,
                            fi.Name,
                            (long)p.NewPartitionBytes,
                            (long)p.NewPartitionBytes,
                            FirmwareFormatKind.AndroidPayloadBin,
                            p.Sha256Hex,
                            null,
                            null,
                            !HashAndBinaryHelpers.IsProtectedPartition(p.PartitionName),
                            HashAndBinaryHelpers.IsProtectedPartition(p.PartitionName),
                            $"Payload Partition ({p.InstallOperationCount} install ops, block={payloadInfo.BlockSize})"));
                    }
                }
                break;
            }

            case FirmwareFormatKind.MediaTekScatterText:
            case FirmwareFormatKind.MediaTekScatterXml:
            {
                var (sInfo, sParts, sWarn) = MediaTekAndUnisocParsers.ParseMediaTekScatter(filePath);
                scatterInfo = sInfo;
                chipsetHint = sInfo.PlatformChipset;
                modelHint = sInfo.ProjectName;
                partitions.AddRange(sParts);
                warnings.AddRange(sWarn);
                missingDeps.Add("Full scatter BROM/Preloader flashing on SLA/DAA-enabled Transsion devices requires an OEM-signed Download Agent (DA); individual unlocked partitions can be flashed via Fastboot/Fastbootd.");
                break;
            }

            case FirmwareFormatKind.UnisocPacContainer:
            {
                using var fs = File.OpenRead(filePath);
                var (pHdr, pParts, _, pWarn) = MediaTekAndUnisocParsers.ParseUnisocPac(fs);
                pacInfo = pHdr;
                modelHint = pHdr?.ProductName;
                partitions.AddRange(pParts);
                warnings.AddRange(pWarn);
                if (pHdr is not null && (!pHdr.HasSignedFdl1 || !pHdr.HasSignedFdl2))
                {
                    missingDeps.Add("PAC container is missing FDL1 or FDL2 entries required for ResearchDownload/FactoryDownload execution.");
                }
                break;
            }

            case FirmwareFormatKind.AndroidSparseImage:
            {
                using var fs = File.OpenRead(filePath);
                sparseInfo = AndroidSparseAndBootParsers.TryParseSparseImage(fs);
                var partName = Path.GetFileNameWithoutExtension(fi.Name);
                long unsparsed = sparseInfo?.TotalUnsparsedBytes ?? fi.Length;
                if (sparseInfo is { AccountingValid: false })
                {
                    warnings.Add("Sparse chunk header block accounting did not match total_blks; image may be truncated.");
                }
                partitions.Add(new PartitionImageMetadata(
                    partName,
                    fi.Name,
                    fi.Length,
                    unsparsed,
                    kind,
                    sha256,
                    null,
                    null,
                    !HashAndBinaryHelpers.IsProtectedPartition(partName),
                    HashAndBinaryHelpers.IsProtectedPartition(partName),
                    $"Sparse v{sparseInfo?.MajorVersion}.{sparseInfo?.MinorVersion} ({sparseInfo?.TotalChunks} chunks -> {unsparsed:N0} B raw)"));
                break;
            }

            case FirmwareFormatKind.AndroidBootImage:
            case FirmwareFormatKind.AndroidVendorBootImage:
            {
                using var fs = File.OpenRead(filePath);
                bootInfo = AndroidSparseAndBootParsers.TryParseBootOrVendorBoot(fs);
                var partName = Path.GetFileNameWithoutExtension(fi.Name);
                partitions.Add(new PartitionImageMetadata(
                    partName,
                    fi.Name,
                    fi.Length,
                    fi.Length,
                    kind,
                    sha256,
                    null,
                    null,
                    true,
                    false,
                    bootInfo is not null
                        ? $"{bootInfo.HeaderMagic} v{bootInfo.HeaderVersion} (OS {bootInfo.DecodedOsVersion}, Patch {bootInfo.DecodedSecurityPatch})"
                        : formatSummary));
                break;
            }

            case FirmwareFormatKind.AndroidVbmetaImage:
            case FirmwareFormatKind.RawPartitionImage:
            {
                var partName = Path.GetFileNameWithoutExtension(fi.Name);
                partitions.Add(new PartitionImageMetadata(
                    partName,
                    fi.Name,
                    fi.Length,
                    fi.Length,
                    kind,
                    sha256,
                    null,
                    null,
                    !HashAndBinaryHelpers.IsProtectedPartition(partName),
                    HashAndBinaryHelpers.IsProtectedPartition(partName),
                    formatSummary));
                break;
            }

            default:
                warnings.Add($"File '{fi.Name}' uses an unsupported or encrypted vendor container format.");
                break;
        }

        // Cross-reference compatible profiles based on modelHint, fpHint, or chipsetHint
        var compatibleProfiles = new List<string>();
        foreach (var prof in _profileRepo.GetAllProfiles())
        {
            bool match = false;
            if (!string.IsNullOrWhiteSpace(fpHint) && fpHint.Contains(prof.Codename, StringComparison.OrdinalIgnoreCase))
                match = true;
            else if (!string.IsNullOrWhiteSpace(modelHint) &&
                     (modelHint.Contains(prof.Codename, StringComparison.OrdinalIgnoreCase) ||
                      prof.ModelCodes.Any(mc => modelHint.Contains(mc, StringComparison.OrdinalIgnoreCase))))
                match = true;
            else if (!string.IsNullOrWhiteSpace(chipsetHint) &&
                     string.Equals(chipsetHint, prof.ChipsetCode, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Chipset '{chipsetHint}' matches profile '{prof.ProfileId}', but exact board/project compatibility must still be verified before flashing.");
            }

            if (match)
            {
                compatibleProfiles.Add(prof.ProfileId);
            }
        }

        var result = new FirmwareInspectionResult
        {
            SourcePath = Path.GetFullPath(filePath),
            FileName = fi.Name,
            FileSizeBytes = fi.Length,
            PackageFormat = kind,
            IsFormatSupported = kind != FirmwareFormatKind.Unsupported,
            Sha256Hex = sha256,
            Crc32Hex = crc32,
            TargetChipsetHint = chipsetHint,
            TargetModelHint = modelHint,
            TargetBuildFingerprint = fpHint,
            ArchiveEntries = archiveEntries,
            Partitions = partitions,
            SparseInfo = sparseInfo,
            BootInfo = bootInfo,
            PayloadInfo = payloadInfo,
            PacInfo = pacInfo,
            ScatterInfo = scatterInfo,
            CompatibleProfileIds = compatibleProfiles,
            ParsingWarnings = warnings,
            MissingDependencies = missingDeps
        };

        _logger.LogInfo(
            "FirmwareExplorer",
            $"Inspected '{fi.Name}' -> Format={kind}, Partitions={partitions.Count}, Warnings={warnings.Count}");

        return result;
    }

    public async Task<string> ExtractArchiveEntryAsync(
        string packagePath,
        string entryName,
        string destinationDir,
        CancellationToken ct = default)
    {
        var format = DetectFormat(packagePath);
        if (format == FirmwareFormatKind.ZipArchive)
        {
            // Could also be AndroidOtaZip; TarAndZipArchiveInspector handles both
        }
        var output = await TarAndZipArchiveInspector.ExtractEntrySafeAsync(packagePath, format, entryName, destinationDir, ct).ConfigureAwait(false);
        _logger.LogInfo("ROMStudio", $"Extracted '{entryName}' -> '{output}'");
        return output;
    }
}
