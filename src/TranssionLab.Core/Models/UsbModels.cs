using TranssionLab.Core.Enums;

namespace TranssionLab.Core.Models;

public sealed record UsbInterfaceDescriptor(
    int InterfaceNumber,
    string InterfaceClass,
    string InterfaceSubClass,
    string InterfaceProtocol,
    string? FriendlyName,
    ConnectionMode MatchedMode);

public sealed record ComPortDescriptor(
    string PortName,
    string Description,
    ushort? VendorId,
    ushort? ProductId,
    string? PnpDeviceId,
    string? DriverProvider,
    ConnectionMode SuspectedMode);

public sealed record DriverDiagnosticInfo(
    DriverHealthStatus Status,
    int? WindowsConfigManagerErrorCode,
    string? InstalledDriverService,
    string? DriverProvider,
    string? DriverVersion,
    string Summary,
    string SafeRemediationGuidance);

public sealed record DetectedUsbDevice(
    string InstanceId,
    ushort VendorId,
    ushort ProductId,
    int? CompositeInterfaceNumber,
    string? SerialNumber,
    string FriendlyName,
    string ManufacturerLabel,
    string? AssociatedComPort,
    ConnectionMode ClassifiedMode,
    ChipsetVendor DetectedChipsetVendor,
    IReadOnlyList<UsbInterfaceDescriptor> Interfaces,
    DriverDiagnosticInfo DriverDiagnostics,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc)
{
    public string VidPidDisplay => $"VID_{VendorId:X4}&PID_{ProductId:X4}" +
        (CompositeInterfaceNumber.HasValue ? $"&MI_{CompositeInterfaceNumber.Value:D2}" : string.Empty);
}

public enum UsbEventKind
{
    Arrived = 0,
    Removed = 1,
    ModeChanged = 2,
    DriverProblemDetected = 3
}

public sealed record UsbConnectionHistoryEvent(
    DateTimeOffset TimestampUtc,
    UsbEventKind EventKind,
    string InstanceId,
    string VidPid,
    string FriendlyName,
    ConnectionMode Mode,
    string Details);
