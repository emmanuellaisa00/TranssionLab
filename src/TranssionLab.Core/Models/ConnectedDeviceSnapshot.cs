using TranssionLab.Core.Enums;

namespace TranssionLab.Core.Models;

/// <summary>
/// Unified physical device snapshot presented on the Connection Dashboard (Section 3).
/// Never infers ExactModel from ChipsetFamily alone.
/// </summary>
public sealed record ConnectedDeviceSnapshot
{
    public required string DeviceKey { get; init; }
    public string? TransportSerial { get; init; }
    public string? UsbInstanceId { get; init; }
    public string? AssociatedComPort { get; init; }
    public ushort? UsbVendorId { get; init; }
    public ushort? UsbProductId { get; init; }

    public required TrackedValue<string> Manufacturer { get; init; }
    public required TrackedValue<string> ExactModel { get; init; }
    public required TrackedValue<string> ModelAliases { get; init; }
    public required TrackedValue<string> Codename { get; init; }
    public required TrackedValue<string> ChipsetFamily { get; init; }
    public required TrackedValue<string> AndroidVersion { get; init; }
    public required TrackedValue<string> BuildFingerprint { get; init; }
    public required TrackedValue<BootloaderLockState> BootloaderState { get; init; }

    public ConnectionMode ActiveConnectionMode { get; init; }
    public ConnectionDashboardState DashboardState { get; init; }
    public string AuthorizationStatus { get; init; } = "Unknown";
    public string ProtocolProviderStatus { get; init; } = "Unknown";
    public DataProvenance IdentificationConfidence { get; init; } = DataProvenance.Detected;
    public string? MatchedProfileId { get; init; }

    public IReadOnlyList<string> AvailableInterfaces { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedOperationIds { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> RawProperties { get; init; } = new Dictionary<string, string>();
    public string LastConnectionEvent { get; init; } = "Discovered";
    public DateTimeOffset LastUpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string UsbIdsDisplay => UsbVendorId.HasValue && UsbProductId.HasValue
        ? $"{UsbVendorId.Value:X4}:{UsbProductId.Value:X4}"
        : "N/A (Transport bridge)";
}
