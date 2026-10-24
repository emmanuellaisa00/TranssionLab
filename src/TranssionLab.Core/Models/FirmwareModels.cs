using TranssionLab.Core.Enums;

namespace TranssionLab.Core.Models;

public sealed record FirmwareArchiveEntry(
    string RelativePath,
    long CompressedSize,
    long UncompressedSize,
    string? Crc32Hex,
    DateTimeOffset? LastModifiedUtc,
    FirmwareFormatKind DetectedInnerFormat);

public sealed record PartitionImageMetadata(
    string PartitionName,
    string FileName,
    long FileSizeBytes,
    long EffectiveUnsparsedBytes,
    FirmwareFormatKind ImageFormat,
    string? Sha256Hex,
    string? PhysicalStartAddressHex,
    long? DeclaredPartitionCapacityBytes,
    bool IsDownloadSelectedByDefault,
    bool IsProtectedNvOrRadio,
    string FormatSummary);

public sealed record SparseImageHeaderInfo(
    ushort MajorVersion,
    ushort MinorVersion,
    uint BlockSizeBytes,
    uint TotalBlocks,
    uint TotalChunks,
    uint ImageChecksum,
    long TotalUnsparsedBytes,
    int RawChunkCount,
    int FillChunkCount,
    int DontCareChunkCount,
    int CrcChunkCount,
    bool AccountingValid);

public sealed record BootImageHeaderInfo(
    string HeaderMagic, // ANDROID! or VNDRBOOT
    uint HeaderVersion,
    uint KernelSizeBytes,
    uint RamdiskSizeBytes,
    uint SecondOrDtbSizeBytes,
    uint PageSizeBytes,
    string DecodedOsVersion,
    string DecodedSecurityPatch,
    string CommandLine);

public sealed record PayloadManifestInfo(
    ulong PayloadVersion,
    ulong ManifestLengthBytes,
    uint MetadataSignatureLength,
    uint BlockSize,
    uint MinorVersion,
    IReadOnlyList<PayloadPartitionSummary> Partitions);

public sealed record PayloadPartitionSummary(
    string PartitionName,
    ulong NewPartitionBytes,
    string? Sha256Hex,
    int InstallOperationCount);

public sealed record UnisocPacHeaderInfo(
    string PacVersion,
    string ProductName,
    string ProductVersion,
    int FileEntryCount,
    long TotalContainerBytes,
    bool HasSignedFdl1,
    bool HasSignedFdl2);

public sealed record MediaTekScatterInfo(
    string PlatformChipset,
    string ProjectName,
    string StorageType,
    string ScatterVersion,
    int PartitionCount,
    int MissingReferencedFilesCount);

public sealed record FirmwareInspectionResult
{
    public required string SourcePath { get; init; }
    public required string FileName { get; init; }
    public required long FileSizeBytes { get; init; }
    public required FirmwareFormatKind PackageFormat { get; init; }
    public required bool IsFormatSupported { get; init; }
    public string? Sha256Hex { get; init; }
    public string? Crc32Hex { get; init; }
    public string? TargetChipsetHint { get; init; }
    public string? TargetModelHint { get; init; }
    public string? TargetBuildFingerprint { get; init; }
    public IReadOnlyList<FirmwareArchiveEntry> ArchiveEntries { get; init; } = Array.Empty<FirmwareArchiveEntry>();
    public IReadOnlyList<PartitionImageMetadata> Partitions { get; init; } = Array.Empty<PartitionImageMetadata>();
    public SparseImageHeaderInfo? SparseInfo { get; init; }
    public BootImageHeaderInfo? BootInfo { get; init; }
    public PayloadManifestInfo? PayloadInfo { get; init; }
    public UnisocPacHeaderInfo? PacInfo { get; init; }
    public MediaTekScatterInfo? ScatterInfo { get; init; }
    public SuperMetadataInfo? SuperInfo { get; init; }
    public IReadOnlyList<string> CompatibleProfileIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ParsingWarnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingDependencies { get; init; } = Array.Empty<string>();
    public DateTimeOffset InspectedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RomBuildEnvironmentReport(
    string WorkspaceDirectory,
    string HostOperatingSystem,
    bool IsPosixOrWsl2BuildHost,
    long AvailableDiskBytes,
    bool HasMinimumDiskSpace350Gb,
    bool HasRepoManifest,
    bool HasDeviceTree,
    bool HasKernelSourceOrGkiConfig,
    bool HasVendorBlobsDirectory,
    IReadOnlyList<string> ValidationFindings,
    IReadOnlyList<string> RequiredComponentsChecklist,
    bool ReadyForSourceBuild);
