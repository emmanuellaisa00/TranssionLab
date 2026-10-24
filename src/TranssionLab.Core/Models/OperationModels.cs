using TranssionLab.Core.Enums;

namespace TranssionLab.Core.Models;

/// <summary>
/// Operation registry descriptor satisfying Section 4 requirements.
/// </summary>
public sealed record OperationDescriptor
{
    public required string OperationId { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<ConnectionMode> RequiredConnectionModes { get; init; }
    public required bool RequiresVerifiedDeviceProfile { get; init; }
    public required AuthorizationRequirement RequiredAuthorization { get; init; }
    public required int MinAndroidApiOrVersion { get; init; }
    public required int MaxAndroidVersion { get; init; } // Up to 16
    public required FirmwareRequirement RequiredFirmware { get; init; }
    public required bool ModifiesDeviceState { get; init; }
    public required bool MayEraseUserData { get; init; }
    public required bool IsDestructive { get; init; }
    public required bool SupportsSafeCancellation { get; init; }
    public required string VerificationMethod { get; init; }
    public required IReadOnlyList<string> KnownLimitations { get; init; }
}

public sealed record OperationCapabilityEvaluation(
    OperationDescriptor Operation,
    bool IsCurrentlyAvailable,
    string StatusReason,
    IReadOnlyList<string> UnmetPrerequisites);

public sealed record ProtocolCapabilitySummary(
    string ProviderName,
    ConnectionMode PrimaryMode,
    ProviderImplementationStatus ImplementationStatus,
    HardwareVerificationLevel VerificationLevel,
    bool ExternalDependenciesSatisfied,
    string DependencySummary,
    IReadOnlyList<string> SupportedCapabilities,
    IReadOnlyList<string> ExplicitLimitations);
