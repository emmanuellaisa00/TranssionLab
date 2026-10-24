using TranssionLab.Core.Enums;

namespace TranssionLab.Core.Models;

public sealed record AdbBlockPartitionEntry(
    string PartitionName,
    string SymlinkPath,
    string ResolvedBlockNode,
    long? SizeBytes,
    bool IsReadableByCurrentShell);

public sealed record DevicePartitionEntry(
    string PartitionName,
    string BaseName,
    string SlotSuffix,
    long? SizeBytes,
    string FormattedSize,
    string PartitionType,
    bool IsLogicalInSuper,
    bool HasSlot,
    string? DeviceBlockNode,
    PartitionAccessPolicy AccessPolicy,
    bool CanFlashInCurrentMode,
    bool CanEraseInCurrentMode,
    bool CanReadbackInCurrentMode,
    bool CanResizeOrDeleteLogical,
    DataProvenance Provenance,
    string SafetyNotice);

public sealed record DevicePartitionTableSnapshot(
    string DeviceKey,
    string TransportSerial,
    ConnectionMode SourceMode,
    string? ActiveSlot,
    string? SuperPartitionName,
    bool UsesDynamicPartitions,
    int TotalPartitions,
    int LogicalPartitionsCount,
    int ProtectedPartitionsCount,
    IReadOnlyList<DevicePartitionEntry> Partitions,
    DateTimeOffset CapturedAtUtc);

public sealed record PartitionModificationResult(
    string OperationId,
    string ActionType,
    string PartitionName,
    bool Succeeded,
    bool PostVerified,
    string Summary,
    string Transcript,
    string? OutputFilePath,
    string? OutputSha256,
    DateTimeOffset CompletedAtUtc);

public sealed record SuperLogicalPartitionEntry(
    string PartitionName,
    string GroupName,
    uint Attributes,
    long SizeBytes,
    int ExtentCount);

public sealed record SuperMetadataInfo(
    ushort MajorVersion,
    ushort MinorVersion,
    ulong BlockDeviceSizeBytes,
    uint MetadataSlotCount,
    IReadOnlyList<string> Groups,
    IReadOnlyList<SuperLogicalPartitionEntry> LogicalPartitions);
