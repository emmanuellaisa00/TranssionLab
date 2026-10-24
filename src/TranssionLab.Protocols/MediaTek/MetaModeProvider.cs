using TranssionLab.Core.Enums;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Protocols.MediaTek;

/// <summary>
/// Dedicated MediaTek META Mode provider (Section 2D).
/// Reports honest USB interface detection and checks whether an authorized external META library is installed.
/// Never fabricates META connectivity when only a generic or unbacked USB interface is present.
/// </summary>
public sealed class MetaModeProvider : IMetaModeProvider
{
    private readonly IAppSettingsService _settings;
    private readonly IStructuredLogger _logger;

    public string ProviderName => "MediaTek META Mode";

    public ProviderImplementationStatus ImplementationStatus =>
        IsAuthorizedMetaLibraryInstalled(out _)
            ? ProviderImplementationStatus.ReadOnlyDiagnostics
            : ProviderImplementationStatus.DetectionOnly;

    public MetaModeProvider(IAppSettingsService settings, IStructuredLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsAuthorizedMetaLibraryInstalled(out string statusDetail)
    {
        var configuredPath = _settings.Current.OptionalMetaLibraryPath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            statusDetail = "No external licensed MediaTek META communication library (MetaCore.dll / SPMETA_DLL.dll) is configured in Settings. Operating in honest USB Detection-Only mode.";
            return false;
        }

        if (!File.Exists(configuredPath))
        {
            statusDetail = $"Configured META library path does not exist: '{configuredPath}'. Operating in USB Detection-Only mode.";
            return false;
        }

        statusDetail = $"External META library file present at '{Path.GetFileName(configuredPath)}'. OEM SLA/META authentication restrictions still apply on secure devices.";
        return true;
    }

    public IReadOnlyList<MetaInterfaceDetection> DetectMetaInterfaces(IReadOnlyList<DetectedUsbDevice> usbInventory)
    {
        var hasLib = IsAuthorizedMetaLibraryInstalled(out _);
        var detections = new List<MetaInterfaceDetection>();

        foreach (var usb in usbInventory.Where(u => u.ClassifiedMode == ConnectionMode.MediaTekMeta))
        {
            var state = usb.DriverDiagnostics.Status != DriverHealthStatus.Healthy
                ? ConnectionDashboardState.ProtocolUnsupported
                : hasLib
                    ? ConnectionDashboardState.AuthenticationRequired
                    : ConnectionDashboardState.ProtocolUnsupported;

            detections.Add(new MetaInterfaceDetection(
                UsbDevice: usb,
                InterfaceClassification: $"MediaTek META USB Endpoint ({usb.VidPidDisplay}, Port={usb.AssociatedComPort ?? "Unmapped"})",
                ExternalCommunicationProviderAvailable: hasLib,
                ReportedState: state,
                DocumentedCapabilities:
                [
                    "USB VID/PID/MI META endpoint identification",
                    "Windows VCOM / ETS driver health diagnostics",
                    "Read-only AP/Modem build inquiry (requires licensed OEM META DLL + unlocked/authorized target)"
                ],
                Prerequisites:
                [
                    "Signed MediaTek USB VCOM / ETS driver bound without Code 28/52",
                    "Licensed OEM ModemMETA communication provider DLL configured locally",
                    "Device firmware that permits META entry (blocked by SLA/META-lock on many retail HiOS/XOS builds)"
                ]));
        }

        return detections;
    }

    public Task<MetaSessionDiagnosticResult> DiagnoseMetaConnectionAsync(DetectedUsbDevice usbDevice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usbDevice);

        if (usbDevice.ClassifiedMode != ConnectionMode.MediaTekMeta)
        {
            const string msg = "Selected USB endpoint is NOT a recognized MediaTek META interface. META connectivity will not be fabricated.";
            _logger.LogWarning(ProviderName, "DiagnoseMetaConnection", msg, usbDevice.InstanceId, "meta.diagnose_interface");
            return Task.FromResult(new MetaSessionDiagnosticResult(
                UsbEndpointVerified: false,
                CommunicationProviderAvailable: false,
                SessionEstablished: false,
                State: ConnectionDashboardState.ProtocolUnsupported,
                DiagnosticSummary: msg,
                Limitations: ["Generic MTP/ADB/VCOM devices cannot be treated as active META sessions."]));
        }

        var hasLib = IsAuthorizedMetaLibraryInstalled(out var libStatus);
        var driverHealthy = usbDevice.DriverDiagnostics.Status == DriverHealthStatus.Healthy;

        var summary = !driverHealthy
            ? $"META USB descriptor detected ({usbDevice.VidPidDisplay}), but Windows driver reports: {usbDevice.DriverDiagnostics.Summary}"
            : !hasLib
                ? $"META USB descriptor verified ({usbDevice.VidPidDisplay} on {usbDevice.AssociatedComPort ?? "USB"}), but active protocol communication is unavailable: {libStatus}"
                : $"META USB descriptor verified ({usbDevice.VidPidDisplay}). Active META session requires vendor META authentication certificate on modern Transsion builds.";

        var resultState = !driverHealthy || !hasLib
            ? ConnectionDashboardState.ProtocolUnsupported
            : ConnectionDashboardState.AuthenticationRequired;

        _logger.Log(new StructuredLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = _logger.CurrentSessionId,
            SequenceNumber = 0,
            Severity = LogSeverity.Info,
            DeviceId = usbDevice.InstanceId,
            ProtocolProvider = ProviderName,
            UsbInterface = usbDevice.VidPidDisplay,
            OperationId = "meta.diagnose_interface",
            RequestedAction = "Verify META Mode Interface & Provider Readiness",
            VerificationResult = summary
        });

        return Task.FromResult(new MetaSessionDiagnosticResult(
            UsbEndpointVerified: true,
            CommunicationProviderAvailable: hasLib,
            SessionEstablished: false,
            State: resultState,
            DiagnosticSummary: summary,
            Limitations:
            [
                "TranssionLab does not bundle proprietary MediaTek ModemMETA binaries.",
                "Protected NVRAM / IMEI / RF calibration modifications are not implemented."
            ]));
    }
}
