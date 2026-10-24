using TranssionLab.Core.Enums;

namespace TranssionLab.Core.Models;

public sealed record BootloaderUnlockSpec(
    bool OemUnlockToggleSupported,
    bool RequiresFastbootFlashingUnlock,
    bool UsesLegacyOemUnlock,
    bool RequiresManufacturerToken,
    bool SupportsFastbootdLogicalFlashing,
    bool RequiresAvbVbmetaDisableConfirmation,
    string UnlockCommand,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<string> DeviceSpecificSteps,
    IReadOnlyList<string> DataLossAndSecurityWarnings);

public sealed record PartitionRuleSpec(
    string PartitionName,
    bool IsLogicalInSuper,
    bool IsProtectedRadioOrNv,
    bool IsBootCritical,
    long? MaxExpectedBytes);

public sealed record HardwareVerificationRecord(
    bool IsHardwareVerified,
    string? TestedBy,
    DateTimeOffset? TestedAtUtc,
    string? TestedBuildFingerprint,
    string VerificationNotes);

public sealed record DeviceProfile
{
    public required string ProfileId { get; init; }
    public required string Manufacturer { get; init; } // TECNO, Infinix, itel
    public required string MarketingName { get; init; }
    public required IReadOnlyList<string> ModelCodes { get; init; } // e.g. CL6, X6850, S667LN
    public required string Codename { get; init; }
    public required string UiSkin { get; init; } // HiOS, XOS, itelOS
    public required ChipsetVendor ChipsetVendor { get; init; }
    public required string ChipsetFamily { get; init; } // e.g. MediaTek Helio G99 Ultimate, Unisoc T606
    public required string ChipsetCode { get; init; } // e.g. MT6789, UMS9230
    public required string StorageType { get; init; } // UFS 2.2, eMMC 5.1
    public required string PartitionScheme { get; init; } // Virtual A/B Dynamic, A/B, A-only
    public required int MinAndroidVersion { get; init; }
    public required int MaxVerifiedAndroidVersion { get; init; }
    public bool Android16NotesApplicable { get; init; }
    public string? Android16CompatibilityNote { get; init; }
    public bool BromSlaDaaEnforced { get; init; }
    public bool UnisocSignedFdlEnforced { get; init; }
    public required BootloaderUnlockSpec BootloaderPolicy { get; init; }
    public required IReadOnlyList<string> ProtectedPartitions { get; init; }
    public required IReadOnlyList<PartitionRuleSpec> KnownPartitions { get; init; }
    public required IReadOnlyList<ConnectionMode> SupportedModes { get; init; }
    public required HardwareVerificationLevel VerificationLevel { get; init; }
    public required HardwareVerificationRecord HardwareVerification { get; init; }
}
